namespace Atalaya.Agents;

/// <summary>
/// Los criterios de severidad, en UN solo sitio versionado (F12 §D).
/// <para>
/// <b>Por qué existe este fichero.</b> El banco de pruebas de F12 tenía UNA crítica sembrada
/// —credenciales escritas en el código— y la auditoría devolvió <b>siete</b>: los off-by-one y las
/// desreferencias nulas salieron críticas, y justo la crítica de verdad salió <b>alta</b>. La escala
/// no estaba solo inflada: estaba invertida en el peor sitio. La rúbrica anterior cabía en cuatro
/// líneas, no daba un solo ejemplo, y su renglón de crítica terminaba en «error de cálculo de
/// negocio» — que es la puerta por la que entró todo off-by-one.
/// </para>
/// <para>
/// <b>Por qué en su propio fichero y no dentro del brief.</b> Un criterio de clasificación que vive
/// incrustado en la cadena de otro prompt se copia el día que hace falta en un segundo sitio, y a
/// partir de ahí hay dos escalas. Aquí hay una, con nombre, y quien la necesite la cita.
/// </para>
/// <para>
/// <b>A quién se le enseña.</b> Al auditor, que es el único que clasifica: severidad se fija al
/// crear el hallazgo (<c>submit_findings</c>). El verificador NO clasifica —su contrato es
/// <c>submit_verdict(findingUlid, verdict, evidence)</c> y no lleva severidad—, así que no se le
/// manda la rúbrica: enseñarle un criterio que no puede aplicar es gastar tokens en ruido. Si algún
/// día el verificador clasifica, cita <see cref="Text"/> y no escribas una segunda escala.
/// </para>
/// <para>
/// <b>Y no reclasifica nada de lo que ya hay.</b> Estos criterios se aplican a auditorías NUEVAS.
/// Lo ya escrito se reclasifica por el camino humano que existe desde §5.6 —cambiar la severidad
/// deja su entrada en el historial, con autor—; barrer el hub por código reescribiría el juicio de
/// una persona sin que nadie lo hubiera pedido.
/// </para>
/// </summary>
public static class SeverityRubric
{
    /// <summary>
    /// La rúbrica tal y como viaja en el prompt. Los cuatro escalones se definen por el DAÑO, con
    /// ejemplos, y el desempate se dice en voz alta porque es donde se torció la escala.
    /// </summary>
    public const string Text =
        """
        RÚBRICA DE SEVERIDAD — clasifica por el DAÑO, no por lo llamativo del defecto.

        - critica — el daño ya está hecho, o está a un paso y nadie lo va a notar a tiempo:
            · secretos o credenciales en el código o en un fichero versionado: contraseñas, tokens,
              claves de API, cadenas de conexión con contraseña, certificados privados;
            · pérdida o corrupción de datos: escritura que pisa datos buenos, borrado sin vuelta,
              migración que trunca, escritura no atómica de un fichero que ya tenía contenido;
            · vulnerabilidad explotable con entrada no confiable: inyección (SQL, comandos, rutas),
              deserialización insegura, autenticación o autorización que se puede saltar,
              criptografía rota o hecha a mano.
          Ejemplos: `var token = "ghp_ab12…";` en el fuente → critica. Concatenar entrada del
          usuario dentro de un SQL → critica.

        - alta — revienta o miente en el CAMINO NORMAL, con una entrada corriente:
            · desreferencia nula, índice fuera de rango, off-by-one alcanzable sin forzar nada;
            · excepción no controlada que tumba la operación que el usuario acaba de pedir;
            · resultado incorrecto que el usuario se lleva como bueno.
          Ejemplos: `for (i = 0; i <= lista.Count; i++)` → alta. `cliente.Nombre.Trim()` cuando el
          llamador puede pasar `cliente` nulo → alta.

        - media — falla en el camino de ERROR, o incumple un contrato sin romper el camino normal:
            · recurso sin liberar: fichero, conexión, handle, suscripción;
            · catch que se traga la excepción o la relanza perdiendo la traza;
            · contrato incumplido: la documentación dice una cosa y el código hace otra, se devuelve
              null donde se prometía que no;
            · coste que solo se nota con volúmenes grandes: enumeración múltiple, consulta en bucle.
          Ejemplos: un `FileStream` sin `using` → media. Un `catch { }` vacío → media.

        - baja — estilo, eficiencia menor, documentación:
            · nombres, formato, código muerto, imports sobrantes;
            · micro-optimizaciones sin impacto medible;
            · comentario desactualizado que no engaña sobre el contrato.

        CÓMO DESEMPATAR — léelo, es donde se tuerce la escala:
        1. «critica» NO significa «importante»: significa una de las tres cosas de su lista. Un
           defecto muy feo que no sea secreto, ni pérdida de datos, ni vulnerabilidad, NO es critica
           por feo que sea. Un off-by-one es alta; una desreferencia nula es alta.
        2. Unas credenciales en el código son critica aunque quepan en una línea y el fichero sea
           pequeño. El tamaño del defecto no es su daño.
        3. Clasifica ESTE defecto en ESTE código, no su categoría en abstracto: importa a qué se
           llega desde aquí y con qué entrada.
        4. Si dudas entre dos escalones, elige el MENOR y explica el porqué en el impacto. Una
           escala inflada no prioriza nada: si todo es critico, no hay orden que seguir.
        """;
}
