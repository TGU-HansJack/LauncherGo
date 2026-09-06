using System.Globalization;
using System.Text.Json.Nodes;

namespace LauncherGo.Services;

internal enum DiscordBotPhrase
{
    ServerStatus,
    Server,
    Status,
    Version,
    ApiVersion,
    Players,
    World,
    WorldTime,
    Season,
    Address,
    Description,
    Welcome,
    Whitelist,
    Password,
    Uptime,
    Performance,
    OnlinePlayers,
    NoOnlinePlayers,
    Connection,
    Latency,
    Enabled,
    Disabled,
    Running,
    Standby,
    Starting,
    Stopping,
    Stopped,
    Offline,
    Unknown,
    Day,
    Hour,
    Minute,
    Second,
    ShowCommands,
    ShowMyPlayer,
    BindPlayer,
    SendServerCommand,
    ManageServer,
    ShowServerStatus,
    ShowOnlinePlayers,
    StartServer,
    StopServer,
    GetOrSetPassword,
    OptionalProfile,
    PasswordAction,
    ExportMods,
    ExportFormat,
    ExportUniversalMods,
    ExportAllMods,
    RunCustomCommand,
    CustomCommandName,
    ConfiguredCustomCommand
}

/// <summary>
/// Discord command descriptions and result labels follow the language configured in
/// the bound Vintage Story server. Unsupported regional variants fall back to their
/// base language and finally to English.
/// </summary>
internal static class DiscordBotText
{
    private static readonly IReadOnlyDictionary<string, string[]> Packs = BuildPacks();
    private static readonly IReadOnlyDictionary<string, string[]> SeasonPacks = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["Spring", "Summer", "Autumn", "Winter"],
        ["zh-cn"] = ["春季", "夏季", "秋季", "冬季"],
        ["zh-tw"] = ["春季", "夏季", "秋季", "冬季"],
        ["ar"] = ["الربيع", "الصيف", "الخريف", "الشتاء"],
        ["be"] = ["Вясна", "Лета", "Восень", "Зіма"],
        ["cs"] = ["Jaro", "Léto", "Podzim", "Zima"],
        ["da"] = ["Forår", "Sommer", "Efterår", "Vinter"],
        ["de"] = ["Frühling", "Sommer", "Herbst", "Winter"],
        ["es"] = ["Primavera", "Verano", "Otoño", "Invierno"],
        ["fr"] = ["Printemps", "Été", "Automne", "Hiver"],
        ["hu"] = ["Tavasz", "Nyár", "Ősz", "Tél"],
        ["is"] = ["Vor", "Sumar", "Haust", "Vetur"],
        ["it"] = ["Primavera", "Estate", "Autunno", "Inverno"],
        ["ja"] = ["春", "夏", "秋", "冬"],
        ["ko"] = ["봄", "여름", "가을", "겨울"],
        ["nl"] = ["Lente", "Zomer", "Herfst", "Winter"],
        ["no"] = ["Vår", "Sommer", "Høst", "Vinter"],
        ["pl"] = ["Wiosna", "Lato", "Jesień", "Zima"],
        ["pt"] = ["Primavera", "Verão", "Outono", "Inverno"],
        ["ru"] = ["Весна", "Лето", "Осень", "Зима"],
        ["sr"] = ["Пролеће", "Лето", "Јесен", "Зима"]
    };

    internal static string NormalizeLanguage(string? language)
    {
        var value = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        if (Packs.ContainsKey(value)) return value;
        var separator = value.IndexOf('-');
        if (separator > 0 && Packs.ContainsKey(value[..separator])) return value[..separator];
        return "en";
    }

    internal static string Get(string? language, DiscordBotPhrase phrase)
    {
        var code = NormalizeLanguage(language);
        var values = Packs.TryGetValue(code, out var pack) ? pack : Packs["en"];
        var index = (int)phrase;
        return index < values.Length && !string.IsNullOrWhiteSpace(values[index])
            ? values[index]
            : Packs["en"][index];
    }

    internal static string FormatServerStatus(JsonObject data, string? language, DateTimeOffset? now = null)
    {
        var lang = NormalizeLanguage(language);
        var online = ReadInt(data, "onlinePlayers");
        var maximum = ReadInt(data, "maxPlayers");
        var lines = new List<string>
        {
            $"**{Get(lang, DiscordBotPhrase.ServerStatus)}** · `{(now ?? DateTimeOffset.Now).ToLocalTime():HH:mm:ss}`",
            $"**{Escape(ReadString(data, "name"))}**",
            $"{Get(lang, DiscordBotPhrase.Status)}: **{FormatStatus(ReadString(data, "status"), lang)}**",
            $"{Get(lang, DiscordBotPhrase.Version)}: `{Safe(ReadString(data, "version"))}` · {Get(lang, DiscordBotPhrase.ApiVersion)}: `{Safe(ReadString(data, "apiVersion"))}`",
            $"{Get(lang, DiscordBotPhrase.Players)}: **{FormatCount(online)}/{FormatCount(maximum)}**",
            $"{Get(lang, DiscordBotPhrase.World)}: {Escape(ReadString(data, "worldName"))}",
            $"{Get(lang, DiscordBotPhrase.WorldTime)}: {Escape(ReadString(data, "worldTime"))} · {Get(lang, DiscordBotPhrase.Season)}: {FormatSeason(ReadString(data, "season"), lang)}",
            $"{Get(lang, DiscordBotPhrase.Address)}: `{Safe(ReadString(data, "address"))}`",
            $"{Get(lang, DiscordBotPhrase.Whitelist)}: {FormatBoolean(data, "whitelistEnabled", lang)} · {Get(lang, DiscordBotPhrase.Password)}: {FormatBoolean(data, "passwordProtected", lang)}",
            $"{Get(lang, DiscordBotPhrase.Uptime)}: {FormatDuration(ReadDouble(data, "uptimeSeconds"), lang)}"
        };

        var description = ReadString(data, "description");
        if (!string.IsNullOrWhiteSpace(description))
            lines.Add($"{Get(lang, DiscordBotPhrase.Description)}: {Escape(description)}");
        var welcome = ReadString(data, "welcomeMessage");
        if (!string.IsNullOrWhiteSpace(welcome))
            lines.Add($"{Get(lang, DiscordBotPhrase.Welcome)}: {Escape(welcome)}");

        if (data["performance"] is JsonObject performance)
        {
            var tps = ReadDouble(performance, "tps");
            var tick = ReadDouble(performance, "averageTickTimeMs");
            var metrics = new List<string>();
            if (tps is not null) metrics.Add($"TPS `{tps.Value.ToString("0.##", CultureInfo.InvariantCulture)}`");
            if (tick is not null) metrics.Add($"Tick `{tick.Value.ToString("0.##", CultureInfo.InvariantCulture)} ms`");
            if (metrics.Count > 0) lines.Add($"{Get(lang, DiscordBotPhrase.Performance)}: {string.Join(" · ", metrics)}");
        }

        return string.Join('\n', lines);
    }

    internal static string FormatPlayers(JsonObject data, string? language, DateTimeOffset? now = null)
    {
        var lang = NormalizeLanguage(language);
        var players = data["players"] is JsonArray array
            ? array.OfType<JsonObject>()
                .Where(player => ReadBool(player, "playing") ?? ReadBool(player, "online") == true)
                .OrderBy(player => ReadString(player, "name"), StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        var maximum = ReadInt(data, "maxPlayers");
        var lines = new List<string>
        {
            $"**{Get(lang, DiscordBotPhrase.OnlinePlayers)}** · `{(now ?? DateTimeOffset.Now).ToLocalTime():HH:mm:ss}`",
            $"{Get(lang, DiscordBotPhrase.Players)}: **{players.Count.ToString(CultureInfo.InvariantCulture)}/{FormatCount(maximum)}**"
        };
        if (players.Count == 0)
        {
            lines.Add(Get(lang, DiscordBotPhrase.NoOnlinePlayers));
            return string.Join('\n', lines);
        }

        foreach (var player in players)
        {
            var state = Safe(ReadString(player, "connectionState"));
            var ping = ReadInt(player, "pingMs");
            var latency = ping is null ? "-" : $"{ping.Value.ToString(CultureInfo.InvariantCulture)} ms";
            lines.Add($"- **{Escape(ReadString(player, "name"))}** · {Get(lang, DiscordBotPhrase.Connection)}: {Escape(state)} · {Get(lang, DiscordBotPhrase.Latency)}: `{latency}`");
        }
        return string.Join('\n', lines);
    }

    private static string FormatStatus(string value, string language) => value.Trim().ToLowerInvariant() switch
    {
        "rungame" or "running" or "run" => Get(language, DiscordBotPhrase.Running),
        "standby" => Get(language, DiscordBotPhrase.Standby),
        "starting" => Get(language, DiscordBotPhrase.Starting),
        "stopping" => Get(language, DiscordBotPhrase.Stopping),
        "stopped" => Get(language, DiscordBotPhrase.Stopped),
        "offline" => Get(language, DiscordBotPhrase.Offline),
        _ => string.IsNullOrWhiteSpace(value) ? Get(language, DiscordBotPhrase.Unknown) : Escape(value)
    };

    private static string FormatSeason(string value, string language)
    {
        var index = value.Trim().ToLowerInvariant() switch
        {
            "spring" => 0,
            "summer" => 1,
            "autumn" or "fall" => 2,
            "winter" => 3,
            _ => -1
        };
        if (index < 0) return Escape(value);
        var seasons = SeasonPacks.TryGetValue(language, out var localized) ? localized : SeasonPacks["en"];
        return seasons[index];
    }

    private static string FormatBoolean(JsonObject data, string key, string language) => ReadBool(data, key) switch
    {
        true => Get(language, DiscordBotPhrase.Enabled),
        false => Get(language, DiscordBotPhrase.Disabled),
        _ => Get(language, DiscordBotPhrase.Unknown)
    };

    private static string FormatDuration(double? seconds, string language)
    {
        if (seconds is null || !double.IsFinite(seconds.Value) || seconds < 0)
            return Get(language, DiscordBotPhrase.Unknown);
        var span = TimeSpan.FromSeconds(seconds.Value);
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days}{Get(language, DiscordBotPhrase.Day)}");
        if (span.Hours > 0) parts.Add($"{span.Hours}{Get(language, DiscordBotPhrase.Hour)}");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}{Get(language, DiscordBotPhrase.Minute)}");
        if (parts.Count == 0 || span.Seconds > 0) parts.Add($"{span.Seconds}{Get(language, DiscordBotPhrase.Second)}");
        return string.Join(' ', parts);
    }

    private static string ReadString(JsonObject data, string key) => data[key]?.ToString()?.Trim() ?? string.Empty;

    private static int? ReadInt(JsonObject data, string key)
    {
        if (data[key] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue) return (int)longValue;
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return (int)Math.Round(number);
        return null;
    }

    private static double? ReadDouble(JsonObject data, string key)
    {
        if (data[key] is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
        if (value.TryGetValue<long>(out var integer)) return integer;
        return null;
    }

    private static bool? ReadBool(JsonObject data, string key)
    {
        if (data[key] is not JsonValue value) return null;
        return value.TryGetValue<bool>(out var boolean) ? boolean : null;
    }

    private static string FormatCount(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "?";
    private static string Safe(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    private static string Escape(string value) => Safe(value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string[]> BuildPacks()
    {
        var packs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        Add(packs, "en", "Server Status|Server|Status|Version|API Version|Players|World|World Time|Season|Address|Description|Welcome Message|Whitelist|Password|Uptime|Performance|Online Players|No players are online.|Connection|Latency|On|Off|Running|Standby|Starting|Stopping|Stopped|Offline|Unknown|d|h|m|s|Show available commands|Show your bound player information|Bind to an online player|Send a server command|Query or control a server|Show server status|Show online players|Start server|Stop server|Get or set password|Optional profile ID or name|Use get, set <password>, or -|Export installed mods|txt, csv, pdf, md, or xlsx|Export Universal mods|Export all mods|Run a configured custom command|Configured command name|Configured custom command");
        Add(packs, "zh-cn", "服务器状态|服务器|状态|版本|API 版本|玩家|世界|世界时间|季节|地址|描述|欢迎语|白名单|密码|运行时间|性能|在线玩家|当前无在线玩家。|连接状态|延迟|开启|关闭|运行中|待机|启动中|停止中|已停止|离线|未知|天|时|分|秒|显示可用命令|显示已绑定玩家的信息|绑定在线玩家|发送服务端命令|查询或控制服务器|显示服务器状态|显示在线玩家|启动服务器|停止服务器|获取或设置密码|可选的档案 ID 或名称|使用 get、set <密码> 或 -|导出已安装模组|txt、csv、pdf、md 或 xlsx|导出 Universal 模组|导出全部模组|运行已配置的自定义命令|已配置的命令名称|已配置的自定义命令");
        Add(packs, "zh-tw", "伺服器狀態|伺服器|狀態|版本|API 版本|玩家|世界|世界時間|季節|位址|描述|歡迎訊息|白名單|密碼|執行時間|效能|線上玩家|目前沒有線上玩家。|連線狀態|延遲|開啟|關閉|執行中|待命|啟動中|停止中|已停止|離線|未知|天|時|分|秒|顯示可用指令|顯示已綁定玩家資訊|綁定線上玩家|傳送伺服器指令|查詢或控制伺服器|顯示伺服器狀態|顯示線上玩家|啟動伺服器|停止伺服器|取得或設定密碼|可選的設定檔 ID 或名稱|使用 get、set <密碼> 或 -|匯出已安裝模組|txt、csv、pdf、md 或 xlsx|匯出 Universal 模組|匯出所有模組|執行已設定的自訂指令|已設定的指令名稱|已設定的自訂指令");
        Add(packs, "de", "Serverstatus|Server|Status|Version|API-Version|Spieler|Welt|Weltzeit|Jahreszeit|Adresse|Beschreibung|Willkommensnachricht|Whitelist|Passwort|Laufzeit|Leistung|Online-Spieler|Keine Spieler online.|Verbindung|Latenz|An|Aus|Läuft|Bereitschaft|Startet|Stoppt|Gestoppt|Offline|Unbekannt|T|Std.|Min.|Sek.|Verfügbare Befehle anzeigen|Informationen zum verknüpften Spieler anzeigen|Mit einem Online-Spieler verknüpfen|Serverbefehl senden|Server abfragen oder steuern|Serverstatus anzeigen|Online-Spieler anzeigen|Server starten|Server stoppen|Passwort abrufen oder setzen|Optionale Profil-ID oder Name|get, set <Passwort> oder - verwenden|Installierte Mods exportieren|txt, csv, pdf, md oder xlsx|Universal-Mods exportieren|Alle Mods exportieren|Konfigurierten Befehl ausführen|Name des konfigurierten Befehls|Konfigurierter benutzerdefinierter Befehl");
        Add(packs, "es", "Estado del servidor|Servidor|Estado|Versión|Versión de API|Jugadores|Mundo|Hora del mundo|Estación|Dirección|Descripción|Mensaje de bienvenida|Lista blanca|Contraseña|Tiempo activo|Rendimiento|Jugadores conectados|No hay jugadores conectados.|Conexión|Latencia|Sí|No|En ejecución|En espera|Iniciando|Deteniendo|Detenido|Sin conexión|Desconocido|d|h|min|s|Mostrar comandos disponibles|Mostrar información del jugador vinculado|Vincular con un jugador conectado|Enviar un comando al servidor|Consultar o controlar el servidor|Mostrar estado del servidor|Mostrar jugadores conectados|Iniciar servidor|Detener servidor|Obtener o cambiar contraseña|ID o nombre de perfil opcional|Usa get, set <contraseña> o -|Exportar mods instalados|txt, csv, pdf, md o xlsx|Exportar mods Universal|Exportar todos los mods|Ejecutar comando personalizado|Nombre del comando configurado|Comando personalizado configurado");
        Add(packs, "fr", "État du serveur|Serveur|État|Version|Version API|Joueurs|Monde|Heure du monde|Saison|Adresse|Description|Message de bienvenue|Liste blanche|Mot de passe|Durée de fonctionnement|Performances|Joueurs en ligne|Aucun joueur en ligne.|Connexion|Latence|Activé|Désactivé|En cours|En attente|Démarrage|Arrêt en cours|Arrêté|Hors ligne|Inconnu|j|h|min|s|Afficher les commandes disponibles|Afficher les informations du joueur lié|Lier un joueur en ligne|Envoyer une commande serveur|Interroger ou contrôler le serveur|Afficher l'état du serveur|Afficher les joueurs en ligne|Démarrer le serveur|Arrêter le serveur|Obtenir ou définir le mot de passe|ID ou nom de profil facultatif|Utilisez get, set <mot de passe> ou -|Exporter les mods installés|txt, csv, pdf, md ou xlsx|Exporter les mods Universal|Exporter tous les mods|Exécuter une commande personnalisée|Nom de la commande configurée|Commande personnalisée configurée");
        Add(packs, "it", "Stato del server|Server|Stato|Versione|Versione API|Giocatori|Mondo|Ora del mondo|Stagione|Indirizzo|Descrizione|Messaggio di benvenuto|Whitelist|Password|Tempo di attività|Prestazioni|Giocatori online|Nessun giocatore online.|Connessione|Latenza|Attivo|Disattivo|In esecuzione|In attesa|Avvio|Arresto|Arrestato|Offline|Sconosciuto|g|h|min|s|Mostra i comandi disponibili|Mostra le informazioni del giocatore associato|Associa un giocatore online|Invia un comando al server|Interroga o controlla il server|Mostra lo stato del server|Mostra i giocatori online|Avvia il server|Arresta il server|Ottieni o imposta la password|ID o nome profilo facoltativo|Usa get, set <password> o -|Esporta i mod installati|txt, csv, pdf, md o xlsx|Esporta mod Universal|Esporta tutti i mod|Esegui un comando personalizzato|Nome del comando configurato|Comando personalizzato configurato");
        Add(packs, "pt", "Estado do servidor|Servidor|Estado|Versão|Versão da API|Jogadores|Mundo|Hora do mundo|Estação|Endereço|Descrição|Mensagem de boas-vindas|Lista de permissões|Senha|Tempo ativo|Desempenho|Jogadores online|Nenhum jogador online.|Conexão|Latência|Ativado|Desativado|Em execução|Em espera|Iniciando|Parando|Parado|Offline|Desconhecido|d|h|min|s|Mostrar comandos disponíveis|Mostrar informações do jogador vinculado|Vincular a um jogador online|Enviar comando ao servidor|Consultar ou controlar o servidor|Mostrar estado do servidor|Mostrar jogadores online|Iniciar servidor|Parar servidor|Obter ou definir senha|ID ou nome de perfil opcional|Use get, set <senha> ou -|Exportar mods instalados|txt, csv, pdf, md ou xlsx|Exportar mods Universal|Exportar todos os mods|Executar comando personalizado|Nome do comando configurado|Comando personalizado configurado");
        Add(packs, "ru", "Состояние сервера|Сервер|Состояние|Версия|Версия API|Игроки|Мир|Время мира|Сезон|Адрес|Описание|Приветствие|Белый список|Пароль|Время работы|Производительность|Игроки онлайн|Нет игроков онлайн.|Подключение|Задержка|Вкл.|Выкл.|Работает|Ожидание|Запускается|Останавливается|Остановлен|Не в сети|Неизвестно|д|ч|м|с|Показать доступные команды|Показать данные привязанного игрока|Привязать игрока онлайн|Отправить команду серверу|Запросить или управлять сервером|Показать состояние сервера|Показать игроков онлайн|Запустить сервер|Остановить сервер|Получить или задать пароль|Необязательный ID или имя профиля|Используйте get, set <пароль> или -|Экспорт установленных модов|txt, csv, pdf, md или xlsx|Экспорт Universal-модов|Экспорт всех модов|Запустить пользовательскую команду|Имя настроенной команды|Настроенная пользовательская команда");
        Add(packs, "pl", "Stan serwera|Serwer|Stan|Wersja|Wersja API|Gracze|Świat|Czas świata|Pora roku|Adres|Opis|Wiadomość powitalna|Biała lista|Hasło|Czas działania|Wydajność|Gracze online|Brak graczy online.|Połączenie|Opóźnienie|Wł.|Wył.|Działa|Gotowość|Uruchamianie|Zatrzymywanie|Zatrzymany|Offline|Nieznany|d|g|min|s|Pokaż dostępne polecenia|Pokaż dane powiązanego gracza|Powiąż gracza online|Wyślij polecenie serwera|Sprawdź lub kontroluj serwer|Pokaż stan serwera|Pokaż graczy online|Uruchom serwer|Zatrzymaj serwer|Pobierz lub ustaw hasło|Opcjonalny identyfikator lub nazwa profilu|Użyj get, set <hasło> lub -|Eksportuj zainstalowane mody|txt, csv, pdf, md lub xlsx|Eksportuj mody Universal|Eksportuj wszystkie mody|Uruchom własne polecenie|Nazwa skonfigurowanego polecenia|Skonfigurowane własne polecenie");
        Add(packs, "ja", "サーバー状態|サーバー|状態|バージョン|API バージョン|プレイヤー|ワールド|ワールド時間|季節|アドレス|説明|歓迎メッセージ|ホワイトリスト|パスワード|稼働時間|パフォーマンス|オンラインプレイヤー|オンラインのプレイヤーはいません。|接続|遅延|有効|無効|実行中|待機中|起動中|停止中|停止済み|オフライン|不明|日|時間|分|秒|利用可能なコマンドを表示|連携したプレイヤー情報を表示|オンラインプレイヤーと連携|サーバーコマンドを送信|サーバーを照会または操作|サーバー状態を表示|オンラインプレイヤーを表示|サーバーを起動|サーバーを停止|パスワードを取得または設定|任意のプロファイル ID または名前|get、set <パスワード>、または -|インストール済み Mod をエクスポート|txt、csv、pdf、md、xlsx|Universal Mod をエクスポート|すべての Mod をエクスポート|設定済みカスタムコマンドを実行|設定済みコマンド名|設定済みカスタムコマンド");
        Add(packs, "ko", "서버 상태|서버|상태|버전|API 버전|플레이어|월드|월드 시간|계절|주소|설명|환영 메시지|화이트리스트|비밀번호|가동 시간|성능|온라인 플레이어|온라인 플레이어가 없습니다.|연결|지연 시간|켜짐|꺼짐|실행 중|대기|시작 중|중지 중|중지됨|오프라인|알 수 없음|일|시간|분|초|사용 가능한 명령어 표시|연결된 플레이어 정보 표시|온라인 플레이어 연결|서버 명령 전송|서버 조회 또는 제어|서버 상태 표시|온라인 플레이어 표시|서버 시작|서버 중지|비밀번호 확인 또는 설정|선택적 프로필 ID 또는 이름|get, set <비밀번호> 또는 - 사용|설치된 모드 내보내기|txt, csv, pdf, md 또는 xlsx|Universal 모드 내보내기|모든 모드 내보내기|설정된 사용자 명령 실행|설정된 명령 이름|설정된 사용자 명령");
        Add(packs, "ar", "حالة الخادم|الخادم|الحالة|الإصدار|إصدار API|اللاعبون|العالم|وقت العالم|الفصل|العنوان|الوصف|رسالة الترحيب|القائمة البيضاء|كلمة المرور|مدة التشغيل|الأداء|اللاعبون المتصلون|لا يوجد لاعبون متصلون.|الاتصال|زمن الاستجابة|مفعّل|معطّل|يعمل|استعداد|قيد البدء|قيد الإيقاف|متوقف|غير متصل|غير معروف|ي|س|د|ث|عرض الأوامر المتاحة|عرض معلومات اللاعب المرتبط|ربط لاعب متصل|إرسال أمر إلى الخادم|الاستعلام عن الخادم أو التحكم به|عرض حالة الخادم|عرض اللاعبين المتصلين|بدء الخادم|إيقاف الخادم|الحصول على كلمة المرور أو تعيينها|معرّف ملف أو اسم اختياري|استخدم get أو set <كلمة المرور> أو -|تصدير الإضافات المثبتة|txt أو csv أو pdf أو md أو xlsx|تصدير إضافات Universal|تصدير كل الإضافات|تشغيل أمر مخصص|اسم الأمر المُعد|أمر مخصص مُعد");
        Add(packs, "cs", "Stav serveru|Server|Stav|Verze|Verze API|Hráči|Svět|Čas světa|Roční období|Adresa|Popis|Uvítací zpráva|Seznam povolených|Heslo|Doba běhu|Výkon|Hráči online|Žádní hráči nejsou online.|Připojení|Odezva|Zapnuto|Vypnuto|Běží|Pohotovost|Spouštění|Zastavování|Zastaveno|Offline|Neznámé|d|h|min|s|Zobrazit dostupné příkazy|Zobrazit údaje propojeného hráče|Propojit online hráče|Odeslat příkaz serveru|Dotázat se nebo ovládat server|Zobrazit stav serveru|Zobrazit hráče online|Spustit server|Zastavit server|Získat nebo nastavit heslo|Volitelné ID nebo název profilu|Použijte get, set <heslo> nebo -|Exportovat nainstalované mody|txt, csv, pdf, md nebo xlsx|Exportovat Universal mody|Exportovat všechny mody|Spustit vlastní příkaz|Název nastaveného příkazu|Nastavený vlastní příkaz");
        Add(packs, "da", "Serverstatus|Server|Status|Version|API-version|Spillere|Verden|Verdenstid|Årstid|Adresse|Beskrivelse|Velkomstbesked|Hvidliste|Adgangskode|Oppetid|Ydeevne|Online-spillere|Ingen spillere er online.|Forbindelse|Forsinkelse|Til|Fra|Kører|Standby|Starter|Stopper|Stoppet|Offline|Ukendt|d|t|min|s|Vis tilgængelige kommandoer|Vis oplysninger om tilknyttet spiller|Tilknyt en online-spiller|Send en serverkommando|Forespørg eller styr serveren|Vis serverstatus|Vis online-spillere|Start server|Stop server|Hent eller angiv adgangskode|Valgfrit profil-id eller navn|Brug get, set <adgangskode> eller -|Eksportér installerede mods|txt, csv, pdf, md eller xlsx|Eksportér Universal-mods|Eksportér alle mods|Kør brugerdefineret kommando|Navn på konfigureret kommando|Konfigureret brugerdefineret kommando");
        Add(packs, "nl", "Serverstatus|Server|Status|Versie|API-versie|Spelers|Wereld|Wereldtijd|Seizoen|Adres|Beschrijving|Welkomstbericht|Whitelist|Wachtwoord|Uptime|Prestaties|Online spelers|Er zijn geen spelers online.|Verbinding|Latentie|Aan|Uit|Actief|Stand-by|Starten|Stoppen|Gestopt|Offline|Onbekend|d|u|min|s|Beschikbare opdrachten tonen|Informatie van gekoppelde speler tonen|Online speler koppelen|Serveropdracht verzenden|Server opvragen of beheren|Serverstatus tonen|Online spelers tonen|Server starten|Server stoppen|Wachtwoord ophalen of instellen|Optionele profiel-ID of naam|Gebruik get, set <wachtwoord> of -|Geïnstalleerde mods exporteren|txt, csv, pdf, md of xlsx|Universal-mods exporteren|Alle mods exporteren|Aangepaste opdracht uitvoeren|Naam van ingestelde opdracht|Ingestelde aangepaste opdracht");
        Add(packs, "no", "Serverstatus|Server|Status|Versjon|API-versjon|Spillere|Verden|Verdenstid|Årstid|Adresse|Beskrivelse|Velkomstmelding|Hviteliste|Passord|Oppetid|Ytelse|Spillere på nett|Ingen spillere er på nett.|Tilkobling|Forsinkelse|På|Av|Kjører|Ventemodus|Starter|Stopper|Stoppet|Frakoblet|Ukjent|d|t|min|s|Vis tilgjengelige kommandoer|Vis informasjon om tilknyttet spiller|Tilknytt en spiller på nett|Send serverkommando|Spør eller styr serveren|Vis serverstatus|Vis spillere på nett|Start server|Stopp server|Hent eller angi passord|Valgfri profil-ID eller navn|Bruk get, set <passord> eller -|Eksporter installerte mods|txt, csv, pdf, md eller xlsx|Eksporter Universal-mods|Eksporter alle mods|Kjør egendefinert kommando|Navn på konfigurert kommando|Konfigurert egendefinert kommando");
        Add(packs, "hu", "Szerverállapot|Szerver|Állapot|Verzió|API-verzió|Játékosok|Világ|Világidő|Évszak|Cím|Leírás|Üdvözlő üzenet|Engedélyezési lista|Jelszó|Üzemidő|Teljesítmény|Online játékosok|Nincs online játékos.|Kapcsolat|Késleltetés|Be|Ki|Fut|Készenlét|Indítás|Leállítás|Leállítva|Offline|Ismeretlen|n|ó|p|mp|Elérhető parancsok megjelenítése|Kapcsolt játékos adatainak megjelenítése|Online játékos kapcsolása|Szerverparancs küldése|Szerver lekérdezése vagy vezérlése|Szerverállapot megjelenítése|Online játékosok megjelenítése|Szerver indítása|Szerver leállítása|Jelszó lekérése vagy beállítása|Opcionális profilazonosító vagy név|Használat: get, set <jelszó> vagy -|Telepített modok exportálása|txt, csv, pdf, md vagy xlsx|Universal modok exportálása|Minden mod exportálása|Egyéni parancs futtatása|Beállított parancs neve|Beállított egyéni parancs");
        Add(packs, "is", "Staða netþjóns|Netþjónn|Staða|Útgáfa|API-útgáfa|Spilarar|Heimur|Heimstími|Árstíð|Vistfang|Lýsing|Velkomstskilaboð|Hvítlisti|Lykilorð|Keyrslutími|Afköst|Spilarar tengdir|Engir spilarar eru tengdir.|Tenging|Seinkun|Kveikt|Slökkt|Í gangi|Biðstaða|Ræsir|Stöðvar|Stöðvaður|Ótengdur|Óþekkt|d|klst|min|sek|Sýna tiltækar skipanir|Sýna upplýsingar tengds spilara|Tengja spilara sem er inni|Senda netþjónsskipun|Fyrirspurn eða stjórn netþjóns|Sýna stöðu netþjóns|Sýna tengda spilara|Ræsa netþjón|Stöðva netþjón|Sækja eða stilla lykilorð|Valfrjálst auðkenni eða heiti sniðs|Notaðu get, set <lykilorð> eða -|Flytja út uppsett mod|txt, csv, pdf, md eða xlsx|Flytja út Universal mod|Flytja út öll mod|Keyra sérsniðna skipun|Heiti stilltrar skipunar|Stillt sérsniðin skipun");
        Add(packs, "be", "Стан сервера|Сервер|Стан|Версія|Версія API|Гульцы|Свет|Час свету|Сезон|Адрас|Апісанне|Прывітанне|Белы спіс|Пароль|Час працы|Прадукцыйнасць|Гульцы анлайн|Няма гульцоў анлайн.|Злучэнне|Затрымка|Укл.|Выкл.|Працуе|Чаканне|Запускаецца|Спыняецца|Спынены|Не ў сетцы|Невядома|д|г|хв|с|Паказаць даступныя каманды|Паказаць даныя прывязанага гульца|Прывязаць гульца анлайн|Адправіць каманду серверу|Запытаць або кіраваць серверам|Паказаць стан сервера|Паказаць гульцоў анлайн|Запусціць сервер|Спыніць сервер|Атрымаць або задаць пароль|Неабавязковы ID або назва профілю|Выкарыстоўвайце get, set <пароль> або -|Экспартаваць усталяваныя моды|txt, csv, pdf, md або xlsx|Экспартаваць Universal-моды|Экспартаваць усе моды|Запусціць уласную каманду|Назва наладжанай каманды|Наладжаная ўласная каманда");
        Add(packs, "sr", "Статус сервера|Сервер|Статус|Верзија|API верзија|Играчи|Свет|Време света|Годишње доба|Адреса|Опис|Порука добродошлице|Бела листа|Лозинка|Време рада|Перформансе|Играчи на мрежи|Нема играча на мрежи.|Веза|Кашњење|Укљ.|Искљ.|Ради|Приправност|Покретање|Заустављање|Заустављен|Ван мреже|Непознато|д|ч|м|с|Прикажи доступне команде|Прикажи податке повезаног играча|Повежи играча на мрежи|Пошаљи команду серверу|Упитај или контролиши сервер|Прикажи статус сервера|Прикажи играче на мрежи|Покрени сервер|Заустави сервер|Преузми или постави лозинку|Опциони ID или назив профила|Користи get, set <лозинка> или -|Извези инсталиране модове|txt, csv, pdf, md или xlsx|Извези Universal модове|Извези све модове|Покрени прилагођену команду|Назив подешене команде|Подешена прилагођена команда");
        return packs;
    }

    private static void Add(Dictionary<string, string[]> packs, string language, string values)
    {
        var entries = values.Split('|');
        if (entries.Length != Enum.GetValues<DiscordBotPhrase>().Length)
            throw new InvalidOperationException($"Discord language pack '{language}' has {entries.Length} entries; expected {Enum.GetValues<DiscordBotPhrase>().Length}.");
        packs[language] = entries;
    }
}
