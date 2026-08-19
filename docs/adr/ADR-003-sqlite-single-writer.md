# SQLite con un único escritor

**Status:** Accepted

## Decisión
Solo el proceso C# escribe SQLite.

## Motivo
Reduce carreras, locking interproceso y estados contradictorios.

## Consecuencia
Los resultados externos vuelven por contratos y C# los persiste.
