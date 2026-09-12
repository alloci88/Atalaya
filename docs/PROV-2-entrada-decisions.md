> **NO PEGAR ESTA CABECERA.** Este fichero es el borrador de la entrada de `DECISIONS.md` para
> PROV-2. El integrador rellena los huecos marcados con «…», pega desde el `## PROV-2` hasta el
> final, y **borra este fichero**. Los huecos son seis: sitios desacoplados, `if` sustituidos,
> tests añadidos, tests reescritos, horas y duración de la sesión, y número de tests de la suite.

## PROV-2 — Nada habla con Copilot por debajo

Nada del producto habla con Copilot por debajo (**revisa D-786** y **revisa D-821**): de los 125
sitios que PROV-1 midió se desacoplan **«… sitios desacoplados»**, y no se añade ni un proveedor
nuevo — D-786 porque la columna de proveedor de una tarifa deja de ser opcional, y D-821 porque el
`IsBilled` que comparaba contra la cadena `"claude-code"` desaparece. La unidad de coste del dominio
pasa a ser el **importe en dólares**, decimal, calculado **por proveedor + modelo** desde
`model-rates.json`: la columna de proveedor **es obligatoria** y la siembra la rellena —lo que hasta
hoy se sembraba sin ella queda sembrado como Copilot—, y lo que decide si un consumo se factura es
**que exista tarifa para su proveedor y su modelo**, así que Claude Code sigue sin tarifarse por la
razón de siempre —la siembra no le pone ninguna— y no por una excepción escrita en código; el
comportamiento de D-821 es el mismo, la causa ya no. Copilot sigue reportando AI credits, pero la
equivalencia **1 credit = 0,01 $** la declara **su proveedor** y no el dominio, igual que la frase
que se enseña cuando una casa no tiene tarifa, que deja de ser una constante con el nombre de Claude
dentro. La divisa de presentación sigue siendo preferencia de máquina (D-1004) y ahora se resuelve
sola: **credits solo mientras todo el gasto del periodo sea de Copilot**, **dólares en cuanto hay
mezcla**, con una **nota corta de una línea** bajo la primera tarjeta que lo dice — porque a partir
de hoy **sí hay total mezclado que formar**, que es justo lo que F16-RETOQUE-2 daba por imposible; un
modelo sin tarifa sigue marcando el agregado como **parcial** (D-787), y **Ajustes → Tarifas gana una
columna «Proveedor»** delante de «Modelo», sin que nada más de esa pantalla se mueva. Por debajo, el
**vocabulario común se muda entero a `Atalaya.Agents`** —lo de D-775 más el `PromptComposer`, el
catálogo de reglas, el de temáticas, la rúbrica de severidad, las directivas, los resultados, los
eventos del hilo, los estados de la sesión y el catálogo de herramientas— y **`Atalaya.Copilot` pasa
a ser una implementación más**, como la de Claude Code: **ni el proyecto de la aplicación ni el de
dominio referencian ya el SDK de Copilot ni ese proyecto**, y a los proveedores se los conoce **solo
por el registro** (D-776), de modo que un identificador desconocido cae al **de fábrica declarado por
el propio proveedor** y no a una casa escrita a mano. **Seis capacidades más se declaran en el
contrato** —la semántica de tokens de entrada, quién es el de fábrica, quién reclama las sesiones que
no escribieron casa, dónde guarda cada casa su modelo, cómo se dice que no tiene tarifa y el corte en
`unit_done`—, del mismo estilo que `IsOptional`/`IsPresent`/`IThreadedAuditor`/`INarratingAuditor`:
**«… `if` sustituidos»**, y **ningún sitio fuera de una implementación de proveedor contiene ya la
cadena de su identificador**. `unit_done` **dice lo mismo en los dos transportes**: una sola
definición y una sola descripción de cada herramienta en el catálogo de `Atalaya.Agents`, y lo que un
transporte necesite añadir por su terminalidad viaja como **propiedad del transporte**, no como texto
distinto — la frase de más que ya divergía se acaba aquí, y **las 15 guardas que viven en transporte
se quedan donde están**, que repetirlas es el precio conocido de transportar. El `commitAuthor` de
`fixes/*.json` aplica **D-037** en el sitio donde se escribe el fichero —noreply si el correo del
perfil es privado— y los ya escritos **no se reescriben**. Se corrigen además las dos líneas de
ESTADO que PROV-1 dejó marcadas como falsas: el subcomando **`barrido`** de PromptBench **también
gasta cuota de IA** —construye un `ClaudeCodeProvider` real y le llama, y es con el que se corrieron
M1 y M2—, e **`IAssistedFixProvider` hereda de `IAuditorProvider`**: lo separado es el **método**
`FixAsync`, con implementación por defecto que lanza, no el tipo; esta entrega **no toca la
jerarquía**. **«… tests añadidos»** y **«… tests reescritos»**; la suite queda en **«… tests de la
suite»** en verde. Sesión **«… de hh:mm a hh:mm del 2026-09-12, … minutos»**, cuatro agentes de área
en paralelo y un integrador. Ningún proveedor nuevo entra hoy: lo que entra es la puerta.
