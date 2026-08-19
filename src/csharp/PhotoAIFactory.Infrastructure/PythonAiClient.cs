using System.Net.Http.Json;
using System.Text.Json;
using PhotoAIFactory.Application;
using PhotoAIFactory.Contracts;

namespace PhotoAIFactory.Infrastructure;

public sealed class PythonAiClient(HttpClient httpClient, string sessionToken) : IPythonAiClient
{
    private readonly HttpClient _http = httpClient;
    private readonly string _sessionToken = sessionToken;

    private HttpRequestMessage Create(HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = new("Bearer", _sessionToken);
        return request;
    }

    public async Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var request = Create(HttpMethod.Get, "v1/health");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HealthResponse>(ContractJson.Options, cancellationToken))
               ?? throw new InvalidDataException("Empty Python health response");
    }

    public async Task<AiResponse> ExecuteAsync(string route, AiRequest requestBody, CancellationToken cancellationToken = default)
    {
        using var request = Create(HttpMethod.Post, route.TrimStart('/'));
        request.Content = JsonContent.Create(requestBody, options: ContractJson.Options);
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Python worker HTTP {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<AiResponse>(text, ContractJson.Options)
               ?? throw new InvalidDataException("Invalid Python AI response");
    }
}
