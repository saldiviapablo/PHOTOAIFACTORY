# Darktable Control Bridge versionado

**Status:** Accepted con hard gate

## Decisión
Encapsular Darktable detrás de un bridge y no depender de XMP arbitrario no
validado.

## Motivo
`darktable-cli` documenta export, XMP, styles y opciones de exportación, pero no
debe asumirse control estable de cualquier slider interno.

## Gate
DT-01 debe validar el subset de módulos/recetas antes del pipeline completo.
