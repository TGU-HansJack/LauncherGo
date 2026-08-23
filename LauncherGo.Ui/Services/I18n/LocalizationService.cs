using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Collections;
using LauncherGo.Abstractions.Services.I18n;

namespace LauncherGo.Ui.Services.I18n;

public sealed class LocalizationService : ILocalizationService
{
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo SimplifiedChineseCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo SimplifiedChineseResourceCulture = CultureInfo.GetCultureInfo("zh-Hans");
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo FrenchCulture = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo SpanishCulture = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo BrazilianPortugueseCulture = CultureInfo.GetCultureInfo("pt-BR");

    private readonly ResourceManager _resourceManager = new(
        "LauncherGo.Ui.Assets.I18n.Resources",
        typeof(LocalizationService).GetTypeInfo().Assembly);

    private readonly object _sync = new();
    private readonly Lazy<IReadOnlyDictionary<string, string>> _legacyPairKeys;
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> _legacyKeyTranslations;
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LegacyTranslations = BuildLegacyTranslations();
    private CultureInfo _currentCulture = CultureInfo.InvariantCulture;

    public LocalizationService()
    {
        _legacyPairKeys = new Lazy<IReadOnlyDictionary<string, string>>(BuildLegacyPairKeys, true);
        _legacyKeyTranslations = new Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(BuildLegacyKeyTranslations, true);
        SetCulture(CultureInfo.CurrentUICulture);
    }

    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set => SetCulture(value);
    }

    public bool SetLanguage(string languageCode)
    {
        if (!TryGetCulture(languageCode, out var culture))
        {
            return false;
        }

        SetCulture(culture);
        return true;
    }

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var resourceCulture = GetResourceCulture(_currentCulture);
            var localized = _resourceManager.GetString(key, resourceCulture)
                            ?? _resourceManager.GetString(key, CultureInfo.InvariantCulture)
                            ?? key;
            if (_legacyKeyTranslations.Value.TryGetValue(key, out var translations))
            {
                var language = translations.Keys.FirstOrDefault(code =>
                    string.Equals(code, _currentCulture.Name, StringComparison.OrdinalIgnoreCase) ||
                    _currentCulture.Name.StartsWith(code[..2], StringComparison.OrdinalIgnoreCase));
                if (language is not null)
                {
                    return translations[language];
                }
            }

            return localized;
        }
    }

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        return args.Length == 0 ? template : string.Format(_currentCulture, template, args);
    }

    public string Resolve(string simplifiedChinese, string english)
    {
        var pair = MakePairKey(simplifiedChinese, english);
        if (LegacyTranslations.TryGetValue(pair, out var translations))
        {
            var language = translations.Keys.FirstOrDefault(code =>
                string.Equals(code, _currentCulture.Name, StringComparison.OrdinalIgnoreCase) ||
                _currentCulture.Name.StartsWith(code[..2], StringComparison.OrdinalIgnoreCase));
            if (language is not null)
            {
                return translations[language];
            }
        }

        if (_legacyPairKeys.Value.TryGetValue(pair, out var key))
        {
            return this[key];
        }

        return _currentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? simplifiedChinese
            : english;
    }

    private void SetCulture(CultureInfo culture)
    {
        var normalized = NormalizeCulture(culture);
        LanguageChangedEventArgs? change = null;

        lock (_sync)
        {
            if (string.Equals(_currentCulture.Name, normalized.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = _currentCulture;
            _currentCulture = normalized;

            // Commit all culture values before notifying the UI. Subscribers can
            // therefore render a complete language without observing a partial state.
            CultureInfo.CurrentCulture = normalized;
            CultureInfo.CurrentUICulture = normalized;
            CultureInfo.DefaultThreadCurrentCulture = normalized;
            CultureInfo.DefaultThreadCurrentUICulture = normalized;
            change = new LanguageChangedEventArgs(previous, normalized);
        }

        LanguageChanged?.Invoke(this, change);
    }

    private static CultureInfo NormalizeCulture(CultureInfo culture)
    {
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return SimplifiedChineseCulture;
        }

        if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return EnglishCulture;
        }

        if (name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return RussianCulture;
        }

        if (name.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            return GermanCulture;
        }

        if (name.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            return FrenchCulture;
        }

        if (name.StartsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            return SpanishCulture;
        }

        if (name.StartsWith("pl", StringComparison.OrdinalIgnoreCase))
        {
            return PolishCulture;
        }

        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return BrazilianPortugueseCulture;
        }

        return culture;
    }

    private static bool TryGetCulture(string? languageCode, out CultureInfo culture)
    {
        culture = EnglishCulture;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return false;
        }

        try
        {
            culture = NormalizeCulture(CultureInfo.GetCultureInfo(languageCode.Trim()));
            // .NET accepts syntactically valid but uninstalled/custom tags such
            // as "not-a-culture". They cannot provide a stable resource lookup.
            return culture.LCID != 4096;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static CultureInfo GetResourceCulture(CultureInfo culture)
    {
        return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChineseResourceCulture
            : culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? EnglishCulture
                : culture;
    }

    private IReadOnlyDictionary<string, string> BuildLegacyPairKeys()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var english = _resourceManager.GetResourceSet(EnglishCulture, true, true);
        var chinese = _resourceManager.GetResourceSet(SimplifiedChineseResourceCulture, true, true);
        if (english is null || chinese is null)
        {
            return result;
        }

        foreach (DictionaryEntry entry in english)
        {
            if (entry.Key is not string key || entry.Value is not string englishText)
            {
                continue;
            }

            var chineseText = chinese.GetString(key);
            if (!string.IsNullOrEmpty(chineseText))
            {
                result.TryAdd(MakePairKey(chineseText, englishText), key);
            }
        }

        return result;
    }

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildLegacyKeyTranslations()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (pair, translations) in LegacyTranslations)
        {
            if (_legacyPairKeys.Value.TryGetValue(pair, out var key))
            {
                result[key] = translations;
            }
        }

        return result;
    }

    private static string MakePairKey(string simplifiedChinese, string english) =>
        $"{simplifiedChinese}\u001f{english}";

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildLegacyTranslations()
    {
        // A small compatibility catalog for older views that still pass a
        // Chinese/English pair instead of a resource key. New UI strings
        // should be added to the RESX files, not to this fallback list.
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        Add("仪表盘", "Dashboard", "Панель управления", "Dashboard", "Tableau de bord", "Panel", "Panel główny", "Painel");
        Add("关于", "About", "О программе", "Über", "À propos", "Acerca de", "Informacje", "Sobre");
        Add("退出", "Exit", "Выход", "Beenden", "Quitter", "Salir", "Wyjście", "Sair");
        Add("语言", "Language", "Язык", "Sprache", "Langue", "Idioma", "Język", "Idioma");
        Add("主题", "Theme", "Тема", "Design", "Thème", "Tema", "Motyw", "Tema");
        Add("控制台", "Console", "Консоль", "Konsole", "Console", "Consola", "Konsola", "Console");
        Add("反馈", "Feedback", "Обратная связь", "Feedback", "Commentaires", "Comentarios", "Opinie", "Feedback");
        Add("实例", "Instance", "Экземпляр", "Instanz", "Instance", "Instancia", "Instancja", "Instância");
        Add("下载", "Download", "Загрузка", "Download", "Téléchargement", "Descargas", "Pobieranie", "Downloads");
        Add("下载版本", "Downloads", "Загрузки", "Downloads", "Téléchargements", "Descargas", "Pobieranie", "Downloads");
        Add("日志", "Logs", "Журналы", "Protokolle", "Journaux", "Registros", "Dzienniki", "Registros");
        Add("控制台日志过滤", "Console Log Filters", "Фильтры日志 консоли", "Konsolenprotokollfilter", "Filtres des journaux de la console", "Filtros de registros de consola", "Filtry dziennika konsoli", "Filtros de log do console");
        Add("默认过滤规则始终生效；启用下方规则后，匹配的日志不会显示在控制台。", "Built-in filters always remain active. Enabled rules below hide matching lines from the console.", "Встроенные фильтры действуют всегда. Включённые правила ниже скрывают совпадающие строки в консоли.", "Integrierte Filter bleiben immer aktiv. Aktivierte Regeln unten blenden passende Zeilen in der Konsole aus.", "Les filtres intégrés restent toujours actifs. Les règles activées ci-dessous masquent les lignes correspondantes dans la console.", "Los filtros integrados siempre están activos. Las reglas activadas ocultan las líneas coincidentes de la consola.", "Wbudowane filtry są zawsze aktywne. Włączone reguły ukrywają pasujące wiersze w konsoli.", "Os filtros integrados permanecem ativos. As regras ativadas abaixo ocultam as linhas correspondentes do console.");
        Add("搜索过滤规则", "Search filters", "Поиск фильтров", "Filter suchen", "Rechercher des filtres", "Buscar filtros", "Szukaj filtrów", "Pesquisar filtros");
        Add("启用", "Enabled", "Включено", "Aktiviert", "Activé", "Activado", "Włączone", "Ativado");
        Add("匹配模式", "Match mode", "Режим сопоставления", "Abgleichmodus", "Mode de correspondance", "Modo de coincidencia", "Tryb dopasowania", "Modo de correspondência");
        Add("匹配内容", "Match pattern", "Шаблон", "Muster", "Motif", "Patrón", "Wzorzec", "Padrão");
        Add("操作", "Actions", "Действия", "Aktionen", "Actions", "Acciones", "Akcje", "Ações");
        Add("包含", "Contains", "Содержит", "Enthält", "Contient", "Contiene", "Zawiera", "Contém");
        Add("完全匹配", "Exact", "Точное совпадение", "Exakte Übereinstimmung", "Correspondance exacte", "Coincidencia exacta", "Dokładne dopasowanie", "Correspondência exata");
        Add("正则表达式", "Regex", "Регулярное выражение", "Regulärer Ausdruck", "Expression régulière", "Expresión regular", "Wyrażenie regularne", "Expressão regular");
        Add("存档", "Saves", "Сохранения", "Spielstände", "Sauvegardes", "Partidas guardadas", "Zapisy", "Salvamentos");
        Add("自动化", "Automation", "Автоматизация", "Automatisierung", "Automatisation", "Automatización", "Automatyzacja", "Automação");
        Add("模组", "Mods", "Моды", "Mods", "Mods", "Mods", "Mody", "Mods");
        Add("启动服务器", "Start Server", "Запустить сервер", "Server starten", "Démarrer le serveur", "Iniciar servidor", "Uruchom serwer", "Iniciar servidor");
        Add("停止服务器", "Stop Server", "Остановить сервер", "Server stoppen", "Arrêter le serveur", "Detener servidor", "Zatrzymaj serwer", "Parar servidor");
        Add("在线玩家", "Online Players", "Игроки онлайн", "Online-Spieler", "Joueurs en ligne", "Jugadores en línea", "Gracze online", "Jogadores online");
        Add("创建", "Create", "Создать", "Erstellen", "Créer", "Crear", "Utwórz", "Criar");
        Add("管理", "Manage", "Управление", "Verwalten", "Gérer", "Administrar", "Zarządzaj", "Gerenciar");
        Add("配置", "Config", "Настройки", "Konfiguration", "Configuration", "Configuración", "Konfiguracja", "Configuração");
        Add("存档", "Save", "Сохранение", "Speichern", "Sauvegarde", "Guardado", "Zapis", "Salvar");
        Add("模组", "Mod", "Моды", "Mod", "Mods", "Mods", "Mody", "Mods");
        Add("机器人", "Robot", "Бот", "Bot", "Robot", "Bot", "Bot", "Robô");
        Add("仓库", "Repository", "Репозиторий", "Repository", "Dépôt", "Repositorio", "Repozytorium", "Repositório");
        Add("社区", "Community", "Сообщество", "Community", "Communauté", "Comunidad", "Społeczność", "Comunidade");
        Add("删除", "Delete", "Удалить", "Löschen", "Supprimer", "Eliminar", "Usuń", "Excluir");
        Add("刷新", "Refresh", "Обновить", "Aktualisieren", "Actualiser", "Actualizar", "Odśwież", "Atualizar");
        Add("取消", "Cancel", "Отмена", "Abbrechen", "Annuler", "Cancelar", "Anuluj", "Cancelar");
        Add("保存", "Save", "Сохранить", "Speichern", "Enregistrer", "Guardar", "Zapisz", "Salvar");
        Add("浏览", "Browse", "Обзор", "Durchsuchen", "Parcourir", "Examinar", "Przeglądaj", "Procurar");
        Add("导入", "Import", "Импорт", "Importieren", "Importer", "Importar", "Importuj", "Importar");
        Add("启动", "Start", "Запустить", "Starten", "Démarrer", "Iniciar", "Uruchom", "Iniciar");
        Add("停止", "Stop", "Остановить", "Stoppen", "Arrêter", "Detener", "Zatrzymaj", "Parar");
        Add("清空", "Clear", "Очистить", "Leeren", "Effacer", "Limpiar", "Wyczyść", "Limpar");
        Add("运行中", "Running", "Запущено", "Läuft", "En cours", "En ejecución", "Uruchomiono", "Em execução");
        Add("已停止", "Stopped", "Остановлено", "Gestoppt", "Arrêté", "Detenido", "Zatrzymano", "Parado");
        Add("服务器", "Server", "Сервер", "Server", "Serveur", "Servidor", "Serwer", "Servidor");
        Add("端口", "Port", "Порт", "Port", "Port", "Puerto", "Port", "Porta");
        Add("状态", "Status", "Статус", "Status", "État", "Estado", "Stan", "Status");
        Add("错误", "Error", "Ошибка", "Fehler", "Erreur", "Error", "Błąd", "Erro");
        Add("游戏服务端", "Game Server", "Игровой сервер", "Spielserver", "Serveur de jeu", "Servidor de juego", "Serwer gry", "Servidor de jogo");
        Add("直连", "Direct", "Прямое подключение", "Direkt", "Direct", "Directo", "Bezpośrednio", "Direto");

        return result;

        void Add(
            string simplifiedChinese,
            string english,
            string russian,
            string german,
            string french,
            string spanish,
            string polish,
            string portuguese)
        {
            result[MakePairKey(simplifiedChinese, english)] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ru-RU"] = russian,
                ["de-DE"] = german,
                ["fr-FR"] = french,
                ["es-ES"] = spanish,
                ["pl-PL"] = polish,
                ["pt-BR"] = portuguese
            };
        }
    }
}
