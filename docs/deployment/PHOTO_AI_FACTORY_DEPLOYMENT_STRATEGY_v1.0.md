# Deployment Strategy v1.0

## Objetivo UX

El usuario instala **PHOTO AI FACTORY**, no una colección manual de herramientas.

## Diseño

El instalador/launcher administra:

- app C#;
- AI Worker Python/runtime;
- Darktable;
- ComfyUI;
- modelos autorizados;
- workflows;
- manifests;
- runtimes GPU cuando correspondan.

## Dos fases recomendadas

### Installer base
App + componentes mínimos.

### Component Manager
Descarga/valida modelos grandes bajo demanda, con:

- fuente oficial;
- SHA-256;
- licencia;
- resumable download;
- versión fija.

Esto reduce tamaño del instalador y facilita licencias/model updates.

## components.lock.json

Se genera en bootstrap real y fija:

```text
id
version
source
sha256
license
compatibility
```

No usar `latest` en producción.

## Updates

- atomic/side-by-side cuando sea viable;
- health test antes de activar;
- rollback;
- proyectos históricos conservan manifest anterior;
- reprocess con versión nueva = Job nuevo.

## GPL / redistribución

Darktable y ComfyUI se mantendrán como componentes/procesos separados.
El empaquetado comercial final requiere revisión de obligaciones de distribución
y source availability antes de release.

## Windows

WinUI/Windows App SDK y .NET se fijarán a una combinación stable-compatible al
crear el primer build reproducible; después se registran en el lockfile.
