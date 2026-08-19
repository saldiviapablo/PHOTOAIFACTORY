# Benchmark Plan v1.0

## Hardware de referencia

- RTX 4060 Ti 8 GB
- 32 GB RAM
- NVMe
- Sony A7 IV / 33 MP

## Dataset técnico

200–500 imágenes:

- RAW;
- RAW+JPEG;
- JPEG-only;
- ISO bajo/alto;
- LED;
- escenario;
- danza;
- movimiento;
- grupos;
- ojos cerrados;
- blur;
- sub/sobreexposición.

## Golden Set

50–100 imágenes editadas manualmente.

## Métricas

### Preselección
- false reject;
- false accept;
- precision/recall de ojos/foco cuando aplique;
- tasa REVIEW_PRE.

### Revelado
- delta frente a Golden Set;
- clipping;
- exposición;
- WB/color;
- preferencia ciega humana.

### Retouch/denoise
- preservación de textura;
- artefactos;
- piel;
- ruido residual.

### Performance
- tiempo p50/p95;
- RAM peak;
- VRAM peak;
- I/O;
- load/unload time.

## Regla de selección de modelo

No gana el modelo más rápido ni el de mayor benchmark público.

Gana el que produzca el mejor compromiso en **nuestro dataset**, con licencia y
estabilidad compatibles.

## Objetivo de producto

La mayoría de las fotos aprobadas deberán llegar a FINAL sin edición manual
posterior; el porcentaje exacto se fija después del primer benchmark real.
