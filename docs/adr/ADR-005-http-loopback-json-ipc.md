# HTTP loopback + JSON C# ↔ Python

**Status:** Accepted para V1

## Decisión
HTTP/JSON sobre `127.0.0.1`, puerto administrado, token de sesión.

## Motivo
Simplicidad, diagnóstico y buen soporte en C# y Python. Las imágenes viajan por
path, no como payload.

## Alternativas
Named Pipes y gRPC quedan disponibles si benchmarks justifican el cambio.
