# BASELINE

## BUG-0001 [critica] Conexión no liberada en ruta de error
- pilar: errores
- confianza: alta
- primera: 2026-01-10T10:00:00Z
- ultima: 2026-02-01T09:00:00Z
- veces: 3
- ubicacion: src/Db/Pool.cs:120
- descripcion: La conexión no se cierra si una excepción interrumpe el flujo.
- impacto: Fuga de conexiones y agotamiento del pool.
- recomendacion: Envolver en using / try-finally.

## BUG-0002 [alta] Deserialización insegura
- pilar: errores
- confianza: media
- ubicacion: src/Api/Payload.cs:44
- descripcion: Se deserializan tipos arbitrarios de entrada no confiable.

## BUG-0003 [media] Posible desreferencia nula
- pilar: errores
- confianza: baja
- ubicacion: src/Core/Resolver.cs:88

## BUG-0004 [critica] Error de cálculo en importes
- pilar: errores
- confianza: alta
- ubicacion: src/Billing/Invoice.cs:210
- descripcion: Redondeo incorrecto sobre importes con IVA.

## OPT-0001 [media] Enumeración múltiple de IEnumerable
- pilar: optimizacion
- confianza: media
- ubicacion: src/Core/Service.cs:52

## OPT-0002 [baja] Asignación excesiva en bucle caliente
- pilar: optimizacion
- confianza: media
- ubicacion: src/Core/Loop.cs:31

## OPT-0003 [alta] Consulta N+1
- pilar: optimizacion
- confianza: alta
- ubicacion: src/Data/Repo.cs:77

## MEJ-0001 [media] Complejidad excesiva
- pilar: mejoras
- confianza: media
- ubicacion: src/Api/Controller.cs:15

## MEJ-0002 [baja] Nomenclatura inconsistente
- pilar: mejoras
- confianza: baja
- ubicacion: src/Util/Helpers.cs:9

## MEJ-0003 [media] API obsoleta
- pilar: mejoras
- confianza: media
- ubicacion: src/Legacy/OldClient.cs:120
