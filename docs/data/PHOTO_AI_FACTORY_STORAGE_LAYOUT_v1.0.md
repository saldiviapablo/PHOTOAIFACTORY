# Storage Layout v1.0

## Usuario selecciona solo dos rutas

```text
INPUT  = carpeta vigilada
OUTPUT = carpeta de entrega/proyecto
```

## Output visible

```text
<OUTPUT>\
├── FINAL\
├── REVISAR\
└── DESCARTADAS\        # si el perfil decide materializar/copiar vistas
```

## Metadata/archivos administrados

```text
<OUTPUT>\
└── .photo-ai-factory\
    ├── originals\
    │   ├── RAW\
    │   └── JPEG_CAMERA\
    ├── history\
    ├── xmp\
    ├── manifests\
    ├── backups\
    └── project.json
```

## Estado vivo local

```text
%LOCALAPPDATA%\PhotoAIFactory\
├── projects\<project_id>\project.db
├── work\<project_id>\<job_id>\<attempt_id>\
├── logs\
├── models\
└── components\
```

## Política

- Originals: permanentes.
- History/XMP/manifests: permanentes.
- Final JPEG: permanente.
- TIFF/DNG/previews: temporales.
- DB live: local.
- DB snapshots: proyecto/backups.
