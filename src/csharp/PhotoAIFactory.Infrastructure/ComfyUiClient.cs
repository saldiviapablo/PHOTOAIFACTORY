using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PhotoAIFactory.Application;

namespace PhotoAIFactory.Infrastructure;

public sealed class ComfyUiClient(HttpClient httpClient, Uri websocketBaseUri) : IComfyUiClient
{
    private readonly HttpClient _http = httpClient;
    private readonly Uri _wsBase = websocketBaseUri;

    public async Task<string> GetSystemStatsAsync(CancellationToken cancellationToken = default) =>
        await _http.GetStringAsync("system_stats", cancellationToken);

    public async Task<string> SubmitPromptAsync(string workflowJson, string clientId, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(workflowJson);
        var payload = JsonSerializer.Serialize(new { prompt = doc.RootElement, client_id = clientId });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("prompt", content, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(text);
        if (!result.RootElement.TryGetProperty("prompt_id", out var id))
            throw new InvalidDataException($"ComfyUI prompt response lacks prompt_id: {text}");
        return id.GetString() ?? throw new InvalidDataException("Empty prompt_id");
    }

    public async Task WaitForCompletionAsync(string promptId, string clientId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        if (await IsCompleteInHistoryAsync(promptId, linked.Token)) return;

        using var ws = new ClientWebSocket();
        var uri = new Uri(_wsBase, $"ws?clientId={Uri.EscapeDataString(clientId)}");
        await ws.ConnectAsync(uri, linked.Token);
        // A short workflow can finish between POST /prompt and opening /ws.
        // Re-check persisted history after the socket is connected to close that race.
        if (await IsCompleteInHistoryAsync(promptId, linked.Token)) return;

        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult receive;
            do
            {
                receive = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
                if (receive.MessageType == WebSocketMessageType.Close)
                    throw new IOException("ComfyUI WebSocket closed before completion");
                ms.Write(buffer, 0, receive.Count);
            } while (!receive.EndOfMessage);

            if (receive.MessageType != WebSocketMessageType.Text) continue;
            var json = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) continue;
            var type = typeEl.GetString();
            if (!root.TryGetProperty("data", out var data)) continue;
            var msgPrompt = data.TryGetProperty("prompt_id", out var p) ? p.GetString() : null;
            if (!string.Equals(msgPrompt, promptId, StringComparison.Ordinal)) continue;
            if (type == "execution_success")
            {
                // ComfyUI can publish execution_success just before the completed
                // history entry becomes observable.  History is the authoritative
                // success confirmation, so close that persistence race here.
                await WaitForHistoryCompletionAsync(promptId, linked.Token);
                return;
            }
            if (type is "execution_error" or "execution_interrupted")
                throw new InvalidOperationException($"ComfyUI {type}: {json}");
        }
        throw new IOException("ComfyUI WebSocket ended unexpectedly");
    }

    private async Task WaitForHistoryCompletionAsync(string promptId, CancellationToken cancellationToken)
    {
        while (!await IsCompleteInHistoryAsync(promptId, cancellationToken))
            await Task.Delay(25, cancellationToken);
    }

    private async Task<bool> IsCompleteInHistoryAsync(string promptId, CancellationToken cancellationToken)
    {
        var text = await _http.GetStringAsync($"history/{Uri.EscapeDataString(promptId)}", cancellationToken);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty(promptId, out var item)) return false;
        if (!item.TryGetProperty("status", out var status)) return false;

        var statusText = status.TryGetProperty("status_str", out var statusElement)
            ? statusElement.GetString()
            : null;
        if (statusText is "error" or "interrupted")
            throw new InvalidOperationException($"ComfyUI {statusText}: {text}");

        return status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True;
    }

    public Task<string> GetHistoryAsync(string promptId, CancellationToken cancellationToken = default) =>
        _http.GetStringAsync($"history/{Uri.EscapeDataString(promptId)}", cancellationToken);

    public async Task CancelPendingAsync(string promptId, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { delete = new[] { promptId } });
        using var response = await _http.PostAsync(
            "queue",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(
            "interrupt",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
