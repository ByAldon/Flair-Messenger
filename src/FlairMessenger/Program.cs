using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LibreMetaverse;
using LMUUID = LibreMetaverse.UUID;

namespace FlairMessenger;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LoginForm());
    }
}

internal static class AppInfo
{
    public const string Version = "0.4.27";
    public const string Name = "Flair Messenger";
    public const string Tagline = "Messenger for Second Life";
    public const string ProductTitle = Name + " - " + Tagline;
    public const string UserAgent = Name + " " + Version;
}

internal sealed class AppSettings
{
    public bool Remember { get; set; }
    public string LoginName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Location { get; set; } = "last";
    public bool TermsAccepted { get; set; }
    public string SelectedLoginName { get; set; } = "";
    public List<RememberedAccount> Accounts { get; set; } = [];
    public bool MinimizeToTray { get; set; } = true;
}

internal sealed class RememberedAccount
{
    public string LoginName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Location { get; set; } = "last";
    public bool TermsAccepted { get; set; }
    public List<string> ClosedConversationIds { get; set; } = [];
    public Dictionary<string, DateTime> ConversationHistoryCutoffs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal readonly record struct ParsedLoginLocation(string StoredValue, string StartValue, string DisplayText);

internal static class LoginLocationParser
{
    private const string HelpText = "Enter Home, Last location, a region name, or a valid Second Life SLURL.";
    private static readonly HashSet<string> SlurlHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "maps.secondlife.com",
        "slurl.com",
        "www.slurl.com"
    };

    public static bool TryParse(string? input, out ParsedLoginLocation location, out string error)
    {
        location = default;
        error = "";
        var value = input?.Trim() ?? "";
        if (value.Equals("home", StringComparison.OrdinalIgnoreCase))
        {
            location = new ParsedLoginLocation("home", "home", "Home");
            return true;
        }
        if (value.Equals("last", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("last location", StringComparison.OrdinalIgnoreCase))
        {
            location = new ParsedLoginLocation("last", "last", "Last location");
            return true;
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            error = HelpText;
            return false;
        }

        if (value.StartsWith("uri:", StringComparison.OrdinalIgnoreCase))
        {
            var legacyParts = value[4..].Split('&', StringSplitOptions.TrimEntries);
            return TryBuildLocation(legacyParts, out location, out error);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("secondlife", StringComparison.OrdinalIgnoreCase))
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(uri.Host)) parts.Add(Uri.UnescapeDataString(uri.Host));
                parts.AddRange(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString));
                if (parts.Count > 0 && parts[0].Equals("app", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Application links are not login locations. Paste a location SLURL instead.";
                    return false;
                }
                return TryBuildLocation(parts, out location, out error);
            }

            if (uri.Scheme is "http" or "https")
            {
                if (!SlurlHosts.Contains(uri.Host))
                {
                    error = "Only Second Life location links from maps.secondlife.com or slurl.com are accepted.";
                    return false;
                }
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString)
                    .ToList();
                if (parts.Count == 0 || !parts[0].Equals("secondlife", StringComparison.OrdinalIgnoreCase))
                {
                    error = HelpText;
                    return false;
                }
                return TryBuildLocation(parts.Skip(1), out location, out error);
            }

            error = HelpText;
            return false;
        }

        return TryBuildLocation(value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            out location, out error);
    }

    public static string NormalizeStoredLocation(string? value) =>
        TryParse(value, out var location, out _) ? location.StoredValue : "last";

    public static string Display(string? value) =>
        TryParse(value, out var location, out _) ? location.DisplayText : "Last location";

    private static bool TryBuildLocation(IEnumerable<string> rawParts, out ParsedLoginLocation location, out string error)
    {
        location = default;
        error = "";
        var parts = rawParts.Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
        if (parts.Length is < 1 or > 4)
        {
            error = HelpText;
            return false;
        }

        var region = Uri.UnescapeDataString(parts[0]).Trim();
        if (region.Length is < 1 or > 255 || region.Contains('&') || region.Any(char.IsControl))
        {
            error = "The SLURL contains an invalid region name.";
            return false;
        }

        var coordinates = new[] { 128, 128, 0 };
        for (var index = 1; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out var coordinate) || coordinate < 0 || coordinate > 65535)
            {
                error = "SLURL coordinates must be whole numbers between 0 and 65535.";
                return false;
            }
            coordinates[index - 1] = coordinate;
        }

        var rawLocation = $"{region}/{coordinates[0]}/{coordinates[1]}/{coordinates[2]}";
        location = new ParsedLoginLocation(
            rawLocation,
            $"uri:{region}&{coordinates[0]}&{coordinates[1]}&{coordinates[2]}",
            rawLocation);
        return true;
    }
}

internal sealed class ChatRecord
{
    public string ConversationId { get; set; } = "";
    public string ConversationName { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}

internal static class Store
{
    public static readonly string Root = Environment.GetEnvironmentVariable("FLAIR_MESSENGER_HOME") ?? AppContext.BaseDirectory;
    public static readonly string DataDir = Path.Combine(Root, "data");
    public static readonly string SettingsPath = Path.Combine(DataDir, "settings.dat");
    public static readonly string MessagesPath = Path.Combine(DataDir, "messages.dat");
    private static readonly string LegacySettingsPath = Path.Combine(DataDir, "settings.json");
    private static readonly string LegacyMessagesPath = Path.Combine(DataDir, "messages.json");
    public static readonly string IconPath = Path.Combine(Root, "assets", "fmicon.png");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings ReadSettings()
    {
        if (File.Exists(SettingsPath))
            return NormalizeSettings(ReadProtected(SettingsPath, new AppSettings()));

        var settings = ReadJson(LegacySettingsPath, new AppSettings());
        if (!File.Exists(LegacySettingsPath)) return NormalizeSettings(settings);

        settings.Password = UnprotectLegacyPassword(settings.Password);
        settings = NormalizeSettings(settings);
        TryMigrate(LegacySettingsPath, SettingsPath, settings);
        return settings;
    }

    public static void WriteSettings(AppSettings settings) => WriteProtected(SettingsPath, NormalizeSettings(settings));

    public static void SaveClientPreferences(bool minimizeToTray)
    {
        var settings = ReadSettings();
        settings.MinimizeToTray = minimizeToTray;
        WriteSettings(settings);
    }

    public static void SaveSuccessfulLogin(string loginName, string password, string location, bool remember, bool termsAccepted)
    {
        var settings = ReadSettings();
        var existingIndex = settings.Accounts.FindIndex(account =>
            account.LoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase));

        if (remember)
        {
            var closedConversationIds = existingIndex >= 0
                ? settings.Accounts[existingIndex].ClosedConversationIds
                : [];
            var historyCutoffs = existingIndex >= 0
                ? settings.Accounts[existingIndex].ConversationHistoryCutoffs
                : new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var account = new RememberedAccount
            {
                LoginName = loginName.Trim(),
                Password = password,
                Location = LoginLocationParser.NormalizeStoredLocation(location),
                TermsAccepted = termsAccepted,
                ClosedConversationIds = closedConversationIds,
                ConversationHistoryCutoffs = historyCutoffs
            };
            if (existingIndex >= 0) settings.Accounts[existingIndex] = account;
            else settings.Accounts.Add(account);
            settings.SelectedLoginName = account.LoginName;
        }
        else if (existingIndex >= 0)
        {
            settings.Accounts.RemoveAt(existingIndex);
            if (settings.SelectedLoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase))
                settings.SelectedLoginName = settings.Accounts.FirstOrDefault()?.LoginName ?? "";
        }

        // Clear the legacy single-account projection before normalization. Otherwise an
        // account that was just forgotten could be mistaken for data that still needs migration.
        settings.Remember = false;
        settings.LoginName = "";
        settings.Password = "";
        settings.Location = "last";
        settings.TermsAccepted = false;
        WriteSettings(settings);
    }

    public static IReadOnlyCollection<string> ReadClosedConversationIds(string loginName)
    {
        var account = ReadSettings().Accounts.FirstOrDefault(candidate =>
            candidate.LoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase));
        return account?.ClosedConversationIds.ToArray() ?? [];
    }

    public static void WriteClosedConversationIds(string loginName, IEnumerable<string> conversationIds)
    {
        var settings = ReadSettings();
        var account = settings.Accounts.FirstOrDefault(candidate =>
            candidate.LoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase));
        if (account is null) return;

        account.ClosedConversationIds = conversationIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !id.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        WriteSettings(settings);
    }

    public static IReadOnlyDictionary<string, DateTime> ReadConversationHistoryCutoffs(string loginName)
    {
        var account = ReadSettings().Accounts.FirstOrDefault(candidate =>
            candidate.LoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase));
        return account is null
            ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, DateTime>(account.ConversationHistoryCutoffs, StringComparer.OrdinalIgnoreCase);
    }

    public static void WriteConversationState(
        string loginName,
        IEnumerable<string> closedConversationIds,
        IReadOnlyDictionary<string, DateTime> historyCutoffs)
    {
        var settings = ReadSettings();
        var account = settings.Accounts.FirstOrDefault(candidate =>
            candidate.LoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase));
        if (account is null) return;

        account.ClosedConversationIds = closedConversationIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !id.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        account.ConversationHistoryCutoffs = historyCutoffs
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                !pair.Key.Equals("system", StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        WriteSettings(settings);
    }

    public static List<ChatRecord> ReadMessages()
    {
        if (File.Exists(MessagesPath))
            return ReadProtected(MessagesPath, new List<ChatRecord>());

        var messages = ReadJson(LegacyMessagesPath, new List<ChatRecord>());
        if (File.Exists(LegacyMessagesPath))
            TryMigrate(LegacyMessagesPath, MessagesPath, messages);
        return messages;
    }

    public static void WriteMessages(List<ChatRecord> messages) => WriteProtected(MessagesPath, messages);

    private static T ReadJson<T>(string path, T fallback)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback
                : fallback;
        }
        catch { return fallback; }
    }

    private static T ReadProtected<T>(string path, T fallback)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (!File.Exists(path)) return fallback;

            var protectedBytes = Convert.FromBase64String(File.ReadAllText(path));
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(jsonBytes) ?? fallback;
        }
        catch { return fallback; }
    }

    private static void WriteProtected<T>(string path, T value)
    {
        Directory.CreateDirectory(DataDir);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);
        WriteAllTextAtomic(path, Convert.ToBase64String(protectedBytes));
    }

    private static void WriteAllTextAtomic(string path, string value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, value);
        File.Move(temporaryPath, path, true);
    }

    private static void TryMigrate<T>(string legacyPath, string protectedPath, T value)
    {
        try
        {
            WriteProtected(protectedPath, value);
            File.Delete(legacyPath);
        }
        catch
        {
            // Keep the readable legacy file when migration cannot be completed safely.
        }
    }

    private static string UnprotectLegacyPassword(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    private static AppSettings NormalizeSettings(AppSettings settings)
    {
        settings.Accounts ??= [];

        if (settings.Remember && !string.IsNullOrWhiteSpace(settings.LoginName) &&
            !settings.Accounts.Any(account => account.LoginName.Equals(settings.LoginName, StringComparison.OrdinalIgnoreCase)))
        {
            settings.Accounts.Add(new RememberedAccount
            {
                LoginName = settings.LoginName.Trim(),
                Password = settings.Password,
                Location = LoginLocationParser.NormalizeStoredLocation(settings.Location),
                TermsAccepted = settings.TermsAccepted
            });
        }

        var uniqueAccounts = new List<RememberedAccount>();
        foreach (var account in settings.Accounts.Where(account => !string.IsNullOrWhiteSpace(account.LoginName)))
        {
            account.LoginName = account.LoginName.Trim();
            account.Location = LoginLocationParser.NormalizeStoredLocation(account.Location);
            account.ClosedConversationIds = (account.ClosedConversationIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id) && !id.Equals("system", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            account.ConversationHistoryCutoffs = (account.ConversationHistoryCutoffs ?? new Dictionary<string, DateTime>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                    !pair.Key.Equals("system", StringComparison.OrdinalIgnoreCase))
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
            var duplicateIndex = uniqueAccounts.FindIndex(existing =>
                existing.LoginName.Equals(account.LoginName, StringComparison.OrdinalIgnoreCase));
            if (duplicateIndex >= 0) uniqueAccounts[duplicateIndex] = account;
            else uniqueAccounts.Add(account);
        }
        settings.Accounts = uniqueAccounts;

        var selected = settings.Accounts.FirstOrDefault(account =>
                account.LoginName.Equals(settings.SelectedLoginName, StringComparison.OrdinalIgnoreCase))
            ?? settings.Accounts.FirstOrDefault(account =>
                account.LoginName.Equals(settings.LoginName, StringComparison.OrdinalIgnoreCase))
            ?? settings.Accounts.FirstOrDefault();

        settings.Remember = selected is not null;
        settings.SelectedLoginName = selected?.LoginName ?? "";
        settings.LoginName = selected?.LoginName ?? "";
        settings.Password = selected?.Password ?? "";
        settings.Location = selected?.Location ?? "last";
        settings.TermsAccepted = selected?.TermsAccepted ?? false;
        return settings;
    }
}

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(49, 51, 56);
    public static readonly Color Panel = Color.FromArgb(43, 45, 49);
    public static readonly Color Rail = Color.FromArgb(30, 31, 34);
    public static readonly Color Input = Color.FromArgb(56, 58, 64);
    public static readonly Color Accent = Color.FromArgb(88, 101, 242);
    public static readonly Color Text = Color.FromArgb(242, 243, 245);
    public static readonly Color Muted = Color.FromArgb(181, 186, 193);
    public static readonly Color HistoryText = Color.FromArgb(151, 155, 162);
    public static readonly Font Font = new("Segoe UI", 10);
    public static readonly Font Bold = new("Segoe UI", 10, FontStyle.Bold);
    public static readonly Font WindowGlyph = new("Segoe UI Symbol", 11);
}

internal sealed class ThemedButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled)
        {
            base.OnPaint(e);
            return;
        }

        var disabledBackground = Color.FromArgb(70, 76, 150);
        var disabledBorder = Color.FromArgb(94, 101, 180);
        var disabledText = Color.FromArgb(218, 221, 238);
        e.Graphics.Clear(disabledBackground);
        using var border = new Pen(disabledBorder);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            disabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

internal sealed class WindowCloseButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var scale = DeviceDpi / 96f;
        var halfGlyph = 5f * scale;
        var centerX = (ClientSize.Width - 1) / 2f;
        var centerY = (ClientSize.Height - 1) / 2f;
        using var pen = new Pen(ForeColor, 1.35f * scale)
        {
            StartCap = LineCap.Square,
            EndCap = LineCap.Square
        };

        var previousSmoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLine(pen, centerX - halfGlyph, centerY - halfGlyph, centerX + halfGlyph, centerY + halfGlyph);
        e.Graphics.DrawLine(pen, centerX + halfGlyph, centerY - halfGlyph, centerX - halfGlyph, centerY + halfGlyph);
        e.Graphics.SmoothingMode = previousSmoothing;
    }
}

internal sealed class SecondLifeService : IDisposable
{
    private readonly GridClient _client = new();
    private readonly Dictionary<LMUUID, Group> _groups = new();
    private readonly SemaphoreSlim _friendsRefreshLock = new(1, 1);
    private readonly object _groupChatLock = new();
    private readonly HashSet<LMUUID> _joinedGroupChats = [];
    private readonly Dictionary<LMUUID, TaskCompletionSource<bool>> _groupChatWaiters = new();
    private readonly Dictionary<LMUUID, string> _groupChatNames = new();
    private bool _logoutRequested;

    public event Action<string>? Status;
    public event Action<ChatRecord>? MessageReceived;
    public event Action? FriendsChanged;
    public event Action? GroupsChanged;

    public IReadOnlyList<FriendInfo> Friends => _client.Friends.FriendList.Values.ToArray();
    public IReadOnlyDictionary<LMUUID, Group> Groups => _groups;
    public bool IsLoggedIn => _client.Network.Connected;
    public string FriendsLoadStatus { get; private set; } = "Waiting for friend data.";

    public SecondLifeService()
    {
        _client.Self.IM += (_, e) =>
        {
            var im = e.IM;
            if (IsTypingDialog(im.Dialog)) return;

            string text = im.Message ?? "";
            if (string.IsNullOrWhiteSpace(text)) return;

            string fromName = string.IsNullOrWhiteSpace(im.FromAgentName) ? "Second Life" : im.FromAgentName;
            LMUUID fromId = im.FromAgentID;
            LMUUID groupOrSessionId = im.IMSessionID;
            bool isGroup = IsGroupOrSessionMessage(im.GroupIM, im.Dialog, _client.Self.IsGroupMessage(im));
            if (isGroup && groupOrSessionId != LMUUID.Zero)
            {
                lock (_groupChatLock) _joinedGroupChats.Add(groupOrSessionId);
            }
            var conversationId = isGroup
                ? groupOrSessionId != LMUUID.Zero
                    ? $"group:{groupOrSessionId}"
                    : $"group:unknown:{fromId}"
                : fromId.ToString();
            var conversationName = isGroup
                ? ResolveGroupConversationName(groupOrSessionId)
                : fromName;
            MessageReceived?.Invoke(new ChatRecord
            {
                ConversationId = conversationId,
                ConversationName = conversationName,
                Sender = fromName,
                Text = text,
                Time = DateTime.Now
            });
        };
        _client.Friends.FriendOnline += (_, _) => UpdateFriendsStatus();
        _client.Friends.FriendOffline += (_, _) => UpdateFriendsStatus();
        _client.Friends.FriendNames += (_, _) => UpdateFriendsStatus();
        _client.Self.GroupChatJoined += (_, e) => HandleGroupChatJoined(e);
        _client.Groups.CurrentGroups += (_, e) =>
        {
            _groups.Clear();
            foreach (var pair in e.Groups)
            {
                _groups[pair.Key] = pair.Value;
                if (!string.IsNullOrWhiteSpace(pair.Value.Name))
                {
                    lock (_groupChatLock) _groupChatNames[pair.Key] = pair.Value.Name;
                }
            }
            GroupsChanged?.Invoke();
        };
        _client.Network.Disconnected += (_, e) =>
        {
            CancelGroupChatWaiters();
            if (!_logoutRequested)
                Status?.Invoke($"Connection lost: {e.Reason}");
        };
    }

    internal static bool IsTypingDialog(InstantMessageDialog dialog) =>
        dialog is InstantMessageDialog.StartTyping or InstantMessageDialog.StopTyping;

    internal static bool IsGroupOrSessionMessage(bool groupFlag, InstantMessageDialog dialog, bool knownGroupSession) =>
        groupFlag || knownGroupSession || dialog == InstantMessageDialog.SessionSend;

    private string ResolveGroupConversationName(LMUUID sessionId)
    {
        if (sessionId != LMUUID.Zero &&
            _groups.TryGetValue(sessionId, out var group) &&
            !string.IsNullOrWhiteSpace(group.Name))
            return group.Name;

        lock (_groupChatLock)
        {
            if (sessionId != LMUUID.Zero &&
                _groupChatNames.TryGetValue(sessionId, out var sessionName) &&
                !string.IsNullOrWhiteSpace(sessionName))
                return sessionName;
        }

        return sessionId == LMUUID.Zero
            ? "Unidentified group/session chat"
            : $"Group/session chat {sessionId.ToString()[..8]}";
    }

    public string ResolveGroupConversationName(string conversationId, string fallback)
    {
        if (!conversationId.StartsWith("group:", StringComparison.OrdinalIgnoreCase)) return fallback;
        var sessionId = conversationId["group:".Length..];

        foreach (var pair in _groups)
        {
            if (pair.Key.ToString().Equals(sessionId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair.Value.Name))
                return pair.Value.Name;
        }

        lock (_groupChatLock)
        {
            foreach (var pair in _groupChatNames)
            {
                if (pair.Key.ToString().Equals(sessionId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                    return pair.Value;
            }
        }

        return fallback;
    }

    public async Task<(bool Success, string Message)> LoginAsync(string loginName, string password, string start, CancellationToken token)
    {
        if (!LoginLocationParser.TryParse(start, out var loginLocation, out var locationError))
            return (false, locationError);

        var parts = SplitLoginName(loginName);
        Status?.Invoke("Signing in to Second Life...");
        var loginParams = _client.Network.DefaultLoginParams(parts.First, parts.Last, password, AppInfo.Name, AppInfo.Version);
        loginParams.Start = loginLocation.StartValue;
        loginParams.URI = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
        loginParams.UserAgent = AppInfo.UserAgent;
        if (!loginParams.Options.Contains("buddy-list", StringComparer.OrdinalIgnoreCase))
            loginParams.Options.Add("buddy-list");

        try
        {
            var response = await _client.Network.LoginWithResponseAsync(loginParams, token);
            if (response is null)
                return (false, "No login response received from Second Life.");
            if (!response.Success)
                return (false, string.IsNullOrWhiteSpace(response.Message) ? "Login failed." : response.Message);

            Status?.Invoke("Signed in. Retrieving offline IMs...");
            try { await _client.Self.RetrieveInstantMessagesAsync(token); } catch { }
            try { _client.Groups.RequestCurrentGroups(); } catch { }
            _ = RefreshFriendsAfterLoginAsync();
            GroupsChanged?.Invoke();
            return (true, "Signed in to Second Life.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<int> RefreshFriendsAsync(CancellationToken token, bool waitForLoginData = false)
    {
        if (!_client.Network.Connected)
        {
            FriendsLoadStatus = "Friends cannot be loaded while disconnected.";
            FriendsChanged?.Invoke();
            return 0;
        }

        await _friendsRefreshLock.WaitAsync(token);
        try
        {
            FriendsLoadStatus = "Loading friends from Second Life...";
            FriendsChanged?.Invoke();

            var attempts = waitForLoginData ? 6 : 1;
            FriendInfo[] friends = [];
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                friends = _client.Friends.FriendList.Values.ToArray();
                if (friends.Length > 0) break;
                if (attempt + 1 < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(750), token);
            }

            if (friends.Length == 0)
            {
                FriendsLoadStatus = "No friends were returned by Second Life. Select Refresh to try again.";
                FriendsChanged?.Invoke();
                return 0;
            }

            var ids = friends.Select(friend => friend.UUID).Distinct().ToList();
            foreach (var batch in ids.Chunk(90))
                _client.Avatars.RequestAvatarNames(batch.ToList());

            FriendsLoadStatus = $"{friends.Length} friend{(friends.Length == 1 ? "" : "s")} loaded. Resolving names...";
            FriendsChanged?.Invoke();
            return friends.Length;
        }
        catch (OperationCanceledException)
        {
            FriendsLoadStatus = "Friend loading was cancelled.";
            FriendsChanged?.Invoke();
            throw;
        }
        catch (Exception ex)
        {
            FriendsLoadStatus = $"Could not refresh friends: {ex.Message}";
            FriendsChanged?.Invoke();
            return 0;
        }
        finally
        {
            _friendsRefreshLock.Release();
        }
    }

    private async Task RefreshFriendsAfterLoginAsync()
    {
        try
        {
            await RefreshFriendsAsync(CancellationToken.None, waitForLoginData: true);
        }
        catch (Exception)
        {
            // RefreshFriendsAsync reports a user-readable status before returning or rethrowing.
        }
    }

    private void UpdateFriendsStatus()
    {
        var count = _client.Friends.FriendList.Count;
        FriendsLoadStatus = $"{count} friend{(count == 1 ? "" : "s")} loaded.";
        FriendsChanged?.Invoke();
    }

    public bool SendInstantMessage(string avatarId, string text)
    {
        if (!LMUUID.TryParse(avatarId, out var id) || string.IsNullOrWhiteSpace(text)) return false;
        _client.Self.InstantMessage(id, text.Trim());
        return true;
    }

    public async Task<(bool Success, string Message)> SendGroupMessageAsync(string groupId, string text, CancellationToken token)
    {
        if (!LMUUID.TryParse(groupId, out var id))
            return (false, "The selected group has an invalid identifier.");
        if (string.IsNullOrWhiteSpace(text))
            return (false, "Enter a message before sending.");
        if (!_client.Network.Connected)
            return (false, "The message was not sent because Flair Messenger is disconnected.");

        var joined = await EnsureGroupChatJoinedAsync(id, token);
        if (!joined.Success) return joined;

        try
        {
            _client.Self.InstantMessageGroup(id, text.Trim());
            return (true, "Message sent.");
        }
        catch (Exception ex)
        {
            return (false, $"The group message could not be sent: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Message)> EnsureGroupChatJoinedAsync(LMUUID groupId, CancellationToken token)
    {
        try
        {
            if (_client.Self.GroupChatSessions.ContainsKey(groupId))
            {
                lock (_groupChatLock) _joinedGroupChats.Add(groupId);
            }
        }
        catch
        {
            // The local session collection can be updated by the network thread; our event-backed cache remains authoritative.
        }

        TaskCompletionSource<bool> waiter;
        var requestJoin = false;
        lock (_groupChatLock)
        {
            if (_joinedGroupChats.Contains(groupId))
                return (true, "Group chat is ready.");

            if (!_groupChatWaiters.TryGetValue(groupId, out waiter!))
            {
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _groupChatWaiters[groupId] = waiter;
                requestJoin = true;
            }
        }

        if (requestJoin)
        {
            Status?.Invoke("Joining the group chat session...");
            try
            {
                _client.Self.RequestJoinGroupChat(groupId);
            }
            catch (Exception ex)
            {
                lock (_groupChatLock) _groupChatWaiters.Remove(groupId);
                return (false, $"The group chat session could not be opened: {ex.Message}");
            }
        }

        try
        {
            var success = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            return success
                ? (true, "Group chat is ready.")
                : (false, "Second Life did not allow this group chat session to open. Group chat may be disabled or unavailable.");
        }
        catch (TimeoutException)
        {
            return (false, "The group chat session did not open in time. Select the group and try again.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Sending the group message was cancelled.");
        }
        finally
        {
            lock (_groupChatLock)
            {
                if (_groupChatWaiters.TryGetValue(groupId, out var current) && ReferenceEquals(current, waiter))
                    _groupChatWaiters.Remove(groupId);
            }
        }
    }

    private void HandleGroupChatJoined(GroupChatJoinedEventArgs e)
    {
        TaskCompletionSource<bool>? waiter = null;
        var ids = new[] { e.SessionID, e.TmpSessionID }.Where(id => id != LMUUID.Zero).Distinct().ToArray();
        lock (_groupChatLock)
        {
            if (e.Success)
            {
                foreach (var id in ids)
                {
                    _joinedGroupChats.Add(id);
                    if (!string.IsNullOrWhiteSpace(e.SessionName)) _groupChatNames[id] = e.SessionName;
                }
            }

            foreach (var id in ids)
            {
                if (!_groupChatWaiters.TryGetValue(id, out waiter)) continue;
                _groupChatWaiters.Remove(id);
                break;
            }
        }

        waiter?.TrySetResult(e.Success);
        var name = string.IsNullOrWhiteSpace(e.SessionName) ? "group chat" : e.SessionName;
        Status?.Invoke(e.Success ? $"Joined {name}." : $"Could not join {name}.");
    }

    private void CancelGroupChatWaiters()
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_groupChatLock)
        {
            _joinedGroupChats.Clear();
            _groupChatNames.Clear();
            waiters = _groupChatWaiters.Values.ToArray();
            _groupChatWaiters.Clear();
        }
        foreach (var waiter in waiters) waiter.TrySetResult(false);
    }

    public void Logout()
    {
        if (!_client.Network.Connected) return;
        _logoutRequested = true;
        _client.Network.Logout();
    }

    public async Task LogoutAsync(CancellationToken token)
    {
        if (!_client.Network.Connected) return;
        _logoutRequested = true;
        await _client.Network.LogoutAsync(token);
    }

    public void Dispose()
    {
        CancelGroupChatWaiters();
        Logout();
        _client.Dispose();
    }

    private static (string First, string Last) SplitLoginName(string loginName)
    {
        var clean = loginName.Trim();
        if (clean.Contains('.'))
        {
            var dot = clean.Split('.', 2, StringSplitOptions.TrimEntries);
            return (dot[0], string.IsNullOrWhiteSpace(dot[1]) ? "Resident" : dot[1]);
        }
        var parts = clean.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 1 ? (parts[0], "Resident") : (parts[0], parts[1]);
    }
}

internal sealed class LoginForm : Form
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private const string SecondLifeTermsUrl = "https://lindenlab.com/legal/second-life-terms-and-conditions";
    private const string ThirdPartyViewerPolicyUrl = "https://secondlife.com/corporate/third-party-viewers";
    private const string LindenPrivacyUrl = "https://lindenlab.com/privacy";

    private readonly ComboBox _login = new();
    private readonly TextBox _password = Box();
    private readonly ComboBox _location = new();
    private readonly CheckBox _remember = new();
    private readonly Label _error = Label("");
    private readonly Button _loginButton = Button("Login");
    private readonly CheckBox _termsAccepted = new();
    private readonly LinkLabel _policyLinks = new();
    private readonly Panel _loginBody = new();
    private readonly List<RememberedAccount> _rememberedAccounts = [];
    private bool _loadingRememberedAccount;
    private readonly ProgressBar _loginProgress = new()
    {
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 25,
        Visible = false
    };

    public LoginForm()
    {
        Text = AppInfo.ProductTitle;
        Size = new Size(440, 525);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(1);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Icon = AppIcon();

        BuildWindowChrome();

        var settings = Store.ReadSettings();

        AddHeader(_loginBody);
        ConfigureLoginSelector();
        Add(Label("Login name:", 32, 112), _login, 32, 138, 350);
        Add(Label("Password:", 32, 180), _password, 32, 206, 350);
        _password.UseSystemPasswordChar = true;

        _loginBody.Controls.Add(Label("Login location:", 32, 248));
        _location.SetBounds(32, 274, 350, 30);
        _location.DropDownStyle = ComboBoxStyle.DropDown;
        _location.DrawMode = DrawMode.OwnerDrawFixed;
        _location.FlatStyle = FlatStyle.Flat;
        _location.ItemHeight = 24;
        _location.Font = Theme.Font;
        _location.BackColor = Theme.Input;
        _location.ForeColor = Theme.Text;
        _location.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _location.AutoCompleteSource = AutoCompleteSource.ListItems;
        _location.DrawItem += DrawLocationItem;
        _location.Items.AddRange(["Home", "Last location"]);
        _location.SelectedIndex = 1;
        _loginBody.Controls.Add(_location);

        _remember.Text = "Remember details";
        _remember.SetBounds(32, 316, 180, 28);
        _remember.Font = Theme.Font;
        _remember.ForeColor = Theme.Muted;
        _remember.BackColor = Theme.Bg;
        _remember.TextAlign = ContentAlignment.MiddleLeft;
        _loginBody.Controls.Add(_remember);

        _loginButton.SetBounds(246, 316, 136, 36);
        _loginButton.Enabled = _termsAccepted.Checked;
        _loginButton.Click += LoginClicked;
        _loginBody.Controls.Add(_loginButton);

        _termsAccepted.Text = "I accept the current Second Life terms and policies.";
        _termsAccepted.SetBounds(32, 358, 350, 28);
        _termsAccepted.Font = Theme.Font;
        _termsAccepted.ForeColor = Theme.Muted;
        _termsAccepted.BackColor = Theme.Bg;
        _termsAccepted.TextAlign = ContentAlignment.MiddleLeft;
        _termsAccepted.CheckedChanged += (_, _) => _loginButton.Enabled = _termsAccepted.Checked;
        _loginBody.Controls.Add(_termsAccepted);

        ConfigurePolicyLinks();
        _policyLinks.SetBounds(32, 390, 350, 24);
        _loginBody.Controls.Add(_policyLinks);

        _loginProgress.SetBounds(32, 425, 350, 8);
        _loginBody.Controls.Add(_loginProgress);

        _error.SetBounds(32, 441, 350, 36);
        _error.ForeColor = Color.FromArgb(248, 113, 113);
        _loginBody.Controls.Add(_error);

        LoadRememberedAccounts(settings);
    }

    private void ConfigureLoginSelector()
    {
        _login.DropDownStyle = ComboBoxStyle.DropDown;
        _login.DrawMode = DrawMode.OwnerDrawFixed;
        _login.FlatStyle = FlatStyle.Flat;
        _login.ItemHeight = 24;
        _login.Font = new Font("Segoe UI", 11);
        _login.BackColor = Theme.Input;
        _login.ForeColor = Theme.Text;
        _login.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _login.AutoCompleteSource = AutoCompleteSource.ListItems;
        _login.DrawItem += DrawLoginItem;
        _login.SelectedIndexChanged += (_, _) => LoadSelectedAccount();
        _login.TextUpdate += (_, _) => LoginNameEdited();
    }

    private void LoadRememberedAccounts(AppSettings settings)
    {
        _rememberedAccounts.Clear();
        _rememberedAccounts.AddRange(settings.Accounts);
        _login.Items.Clear();
        _login.Items.AddRange(_rememberedAccounts.Select(account => account.LoginName).Cast<object>().ToArray());

        var selectedIndex = _rememberedAccounts.FindIndex(account =>
            account.LoginName.Equals(settings.SelectedLoginName, StringComparison.OrdinalIgnoreCase));
        if (selectedIndex < 0 && _rememberedAccounts.Count > 0) selectedIndex = 0;
        if (selectedIndex >= 0) _login.SelectedIndex = selectedIndex;
    }

    private void LoadSelectedAccount()
    {
        if (_login.SelectedIndex < 0 || _login.SelectedIndex >= _rememberedAccounts.Count) return;
        var account = _rememberedAccounts[_login.SelectedIndex];
        _loadingRememberedAccount = true;
        try
        {
            _login.Text = account.LoginName;
            _password.Text = account.Password;
            if (account.Location == "home") _location.SelectedIndex = 0;
            else if (account.Location == "last") _location.SelectedIndex = 1;
            else
            {
                _location.SelectedIndex = -1;
                _location.Text = account.Location;
            }
            _remember.Checked = true;
            _termsAccepted.Checked = account.TermsAccepted;
        }
        finally
        {
            _loadingRememberedAccount = false;
        }
    }

    private void LoginNameEdited()
    {
        if (_loadingRememberedAccount) return;

        var exactIndex = _rememberedAccounts.FindIndex(account =>
            account.LoginName.Equals(_login.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exactIndex >= 0)
        {
            _login.SelectedIndex = exactIndex;
            return;
        }

        _password.Clear();
        _location.SelectedIndex = 1;
        _remember.Checked = false;
        _termsAccepted.Checked = false;
    }

    private void BuildWindowChrome()
    {
        var windowLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Bg
        };
        windowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        windowLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        windowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(windowLayout);

        var titleBar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Rail };
        windowLayout.Controls.Add(titleBar, 0, 0);

        var titleLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Rail
        };
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        titleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        titleBar.Controls.Add(titleLayout);

        var iconHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 8, 8),
            Margin = Padding.Empty,
            BackColor = Theme.Rail
        };
        var icon = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = LoadImageCopy(Store.IconPath),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = Padding.Empty,
            BackColor = Theme.Rail
        };
        iconHost.Controls.Add(icon);
        titleLayout.Controls.Add(iconHost, 0, 0);

        var title = Label(AppInfo.ProductTitle, 0, 0, 300, 42, 9);
        title.Dock = DockStyle.Fill;
        title.Margin = new Padding(0, 9, 0, 9);
        title.Padding = new Padding(2, 0, 0, 0);
        title.AutoSize = false;
        title.AutoEllipsis = true;
        title.ForeColor = Theme.Muted;
        title.BackColor = Theme.Rail;
        titleLayout.Controls.Add(title, 1, 0);

        var windowButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Rail
        };
        windowButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        windowButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        windowButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        titleLayout.Controls.Add(windowButtons, 2, 0);

        var minimize = WindowButton("—", "Minimize");
        var close = WindowButton("×", "Close");
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        close.FlatAppearance.MouseDownBackColor = Color.FromArgb(160, 32, 24);
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        close.Click += (_, _) => Close();
        windowButtons.Controls.Add(minimize, 0, 0);
        windowButtons.Controls.Add(close, 1, 0);

        foreach (var dragSurface in new Control[] { titleBar, titleLayout, iconHost, icon, title })
            dragSurface.MouseDown += BeginWindowDrag;

        _loginBody.Dock = DockStyle.Fill;
        _loginBody.Margin = Padding.Empty;
        _loginBody.BackColor = Theme.Bg;
        windowLayout.Controls.Add(_loginBody, 0, 1);
    }

    private static Image? LoadImageCopy(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var source = Image.FromFile(path);
            return new Bitmap(source);
        }
        catch
        {
            return null;
        }
    }

    private static Button WindowButton(string text, string accessibleName)
    {
        var button = accessibleName == "Close" ? new WindowCloseButton() : new Button();
        button.Text = accessibleName == "Close" ? "" : text;
        button.Dock = DockStyle.Fill;
        button.Margin = Padding.Empty;
        button.Padding = Padding.Empty;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Theme.Rail;
        button.ForeColor = Theme.Text;
        button.Font = Theme.WindowGlyph;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.MinimumSize = accessibleName == "Close" ? new Size(44, 32) : Size.Empty;
        button.TabStop = false;
        button.UseVisualStyleBackColor = false;
        button.AccessibleName = accessibleName;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Input;
        button.FlatAppearance.MouseDownBackColor = Theme.Panel;
        return button;
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    private async void LoginClicked(object? sender, EventArgs e)
    {
        if (!_termsAccepted.Checked)
        {
            _error.Text = "Accept the Second Life terms and policies before signing in.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_login.Text) || string.IsNullOrWhiteSpace(_password.Text))
        {
            _error.Text = "Enter your login name and password.";
            return;
        }

        if (!LoginLocationParser.TryParse(_location.Text, out var parsedLocation, out var locationError))
        {
            _error.ForeColor = Color.FromArgb(248, 113, 113);
            _error.Text = locationError;
            return;
        }

        _loginButton.Enabled = false;
        _loginProgress.Visible = true;
        _error.ForeColor = Theme.Muted;
        _error.Text = "Connecting to Second Life...";

        var loginName = _login.Text.Trim();
        var password = _password.Text;
        var location = parsedLocation.StoredValue;
        var remember = _remember.Checked;
        var termsAccepted = _termsAccepted.Checked;

        var service = new SecondLifeService();
        service.Status += UpdateLoginStatus;
        var result = await service.LoginAsync(loginName, password, location, CancellationToken.None);
        service.Status -= UpdateLoginStatus;
        if (!result.Success)
        {
            service.Dispose();
            _loginProgress.Visible = false;
            _error.ForeColor = Color.FromArgb(248, 113, 113);
            _error.Text = result.Message;
            _loginButton.Enabled = _termsAccepted.Checked;
            return;
        }

        try
        {
            Store.SaveSuccessfulLogin(loginName, password, location, remember, termsAccepted);
        }
        catch
        {
            // A local storage failure must not terminate an authenticated session.
        }

        _error.Text = "Opening Flair Messenger...";
        Hide();
        var mainForm = new MainForm(service, loginName, location);
        mainForm.FormClosed += (_, _) => Close();
        mainForm.Show();
    }

    private void UpdateLoginStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateLoginStatus(text));
            return;
        }

        _error.Text = text;
    }

    private void ConfigurePolicyLinks()
    {
        _policyLinks.Text = "Terms | Third-Party Viewer Policy | Privacy";
        _policyLinks.Font = Theme.Font;
        _policyLinks.TextAlign = ContentAlignment.MiddleLeft;
        _policyLinks.LinkColor = Color.FromArgb(147, 197, 253);
        _policyLinks.ActiveLinkColor = Color.White;
        _policyLinks.VisitedLinkColor = _policyLinks.LinkColor;
        _policyLinks.BackColor = Theme.Bg;

        _policyLinks.Links.Add(0, 5, SecondLifeTermsUrl);
        _policyLinks.Links.Add(8, 25, ThirdPartyViewerPolicyUrl);
        _policyLinks.Links.Add(36, 7, LindenPrivacyUrl);
        _policyLinks.LinkClicked += (_, e) => OpenPolicyLink(e.Link?.LinkData as string);
    }

    private void DrawLocationItem(object? sender, DrawItemEventArgs e)
    {
        var isDropDownItem = (e.State & DrawItemState.ComboBoxEdit) == 0;
        var isHighlighted = isDropDownItem && (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(isHighlighted ? Theme.Accent : Theme.Input);
        e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index >= 0 && e.Index < _location.Items.Count)
        {
            var textBounds = new Rectangle(e.Bounds.X + 7, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                _location.Items[e.Index]?.ToString() ?? "",
                Theme.Font,
                textBounds,
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void DrawLoginItem(object? sender, DrawItemEventArgs e)
    {
        var isHighlighted = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(isHighlighted ? Theme.Accent : Theme.Input);
        e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index >= 0 && e.Index < _login.Items.Count)
        {
            var textBounds = new Rectangle(e.Bounds.X + 7, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                _login.Items[e.Index]?.ToString() ?? "",
                _login.Font,
                textBounds,
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private static void OpenPolicyLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // The login screen remains usable when Windows cannot open the default browser.
        }
    }

    private static void AddHeader(Control parent)
    {
        Image? logoImage = null;
        if (File.Exists(Store.IconPath))
        {
            try
            {
                using var source = Image.FromFile(Store.IconPath);
                logoImage = new Bitmap(source);
            }
            catch
            {
                // The login form remains usable when the optional image cannot be decoded.
            }
        }
        if (logoImage is not null)
        {
            var logo = new PictureBox { Image = logoImage, SizeMode = PictureBoxSizeMode.Zoom };
            logo.SetBounds(26, 24, 64, 64);
            parent.Controls.Add(logo);
        }
        var textLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Bg
        };
        textLayout.SetBounds(106, 20, 300, 76);
        textLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        textLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        textLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var productName = Label("Flair Messenger", size: 17, bold: true);
        productName.Dock = DockStyle.Fill;
        productName.Margin = Padding.Empty;
        productName.Padding = Padding.Empty;
        productName.AutoEllipsis = true;
        textLayout.Controls.Add(productName, 0, 0);

        var productDetails = Label($"{AppInfo.Tagline} | v{AppInfo.Version}", size: 9.5f);
        productDetails.Dock = DockStyle.Fill;
        productDetails.Margin = Padding.Empty;
        productDetails.Padding = Padding.Empty;
        productDetails.AutoEllipsis = true;
        textLayout.Controls.Add(productDetails, 0, 1);
        parent.Controls.Add(textLayout);
    }

    internal static Icon AppIcon()
    {
        if (!File.Exists(Store.IconPath)) return (Icon)SystemIcons.Application.Clone();
        try
        {
            using var source = Image.FromFile(Store.IconPath);
            using var bitmap = new Bitmap(source);
            var handle = bitmap.GetHicon();
            try
            {
                using var borrowedIcon = Icon.FromHandle(handle);
                return (Icon)borrowedIcon.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    internal static TextBox Box() => new()
    {
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 11)
    };

    internal static Button Button(string text)
    {
        var button = new ThemedButton
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Bold,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(129, 140, 248);
        return button;
    }

    internal static Label Label(string text, int x = 0, int y = 0, int w = 350, int h = 24, float size = 10, bool bold = false)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = false
        };
        label.SetBounds(x, y, w, h);
        return label;
    }

    private void Add(Label label, Control box, int x, int y, int width)
    {
        _loginBody.Controls.Add(label);
        box.SetBounds(x, y, width, 30);
        _loginBody.Controls.Add(box);
    }
}

internal sealed class MainForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly SecondLifeService _service;
    private readonly string _loginName;
    private readonly string _location;
    private readonly List<ChatRecord> _messages;
    private readonly List<ChatRecord> _notifications;
    private ListBox _conversations = new();
    private ListBox _friendsList = new();
    private ListBox _groupsList = new();
    private readonly RichTextBox _messageFeed = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Panel _clientArea = new();
    private readonly TableLayoutPanel _shell = new();
    private readonly Panel _content = new();
    private readonly Panel _logoutOverlay = new();
    private readonly ProgressBar _logoutProgress = new();
    private readonly Panel _titleBar = new();
    private readonly Label _windowTitle = new();
    private readonly Button _maximizeWindowButton = new();
    private readonly Button _closeChatButton = LoginForm.Button("Close chat");
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _closedConversationIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _conversationHistoryCutoffs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Icon _baseWindowIcon;
    private readonly NotifyIcon _tray;
    private Icon? _unreadBadgeIcon;
    private static readonly TimeSpan RecentConversationWindow = TimeSpan.FromHours(24);
    private readonly DateTime _sessionStartedUtc = DateTime.UtcNow;
    private ConversationItem _active = new("system", "System", ConversationKind.System);
    private bool _logoutStarted;
    private bool _logoutFinished;
    private bool _friendsAutoRefreshAttempted;
    private bool _friendsRefreshRunning;
    private bool _sendingMessage;
    private bool _refreshingConversations;
    private bool _minimizeToTray;
    private string _activePage = "Chats";
    private int _unreadCount;

    public MainForm(SecondLifeService service, string loginName, string location)
    {
        _service = service;
        _loginName = loginName;
        _location = location;
        _minimizeToTray = Store.ReadSettings().MinimizeToTray;
        _closedConversationIds.UnionWith(Store.ReadClosedConversationIds(loginName));
        foreach (var (conversationId, cutoff) in Store.ReadConversationHistoryCutoffs(loginName))
            _conversationHistoryCutoffs[conversationId] = cutoff;
        foreach (var conversationId in _closedConversationIds)
            _conversationHistoryCutoffs.TryAdd(conversationId, _sessionStartedUtc);
        var storedMessages = Store.ReadMessages();
        _messages = storedMessages.Where(message => !IsLegacyTypingArtifact(message)).ToList();
        var historyChanged = _messages.Count != storedMessages.Count;
        foreach (var message in _messages.Where(IsLegacyApplicationSender))
        {
            message.Sender = AppInfo.Name;
            historyChanged = true;
        }
        if (historyChanged) Store.WriteMessages(_messages);
        _notifications = new List<ChatRecord>();

        Text = $"{AppInfo.ProductTitle} v{AppInfo.Version}";
        Size = new Size(1100, 720);
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        _baseWindowIcon = LoginForm.AppIcon();
        Icon = _baseWindowIcon;

        _tray = new NotifyIcon { Icon = _baseWindowIcon, Text = "Flair Messenger", Visible = false, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Items.Add("Open Flair Messenger", null, (_, _) => RestoreFromTray());
        _tray.ContextMenuStrip.Items.Add("Mark all as read", null, (_, _) => ClearUnread());
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Close());
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _closeChatButton.AccessibleName = "Close active chat";
        _closeChatButton.Click += (_, _) => CloseActiveConversation();

        BuildShell();
        WireSecondLifeEvents();
        AddSystem("Signed in to Second Life.");
        ShowChats();
        RefreshAll();
    }

    private static bool IsLegacyTypingArtifact(ChatRecord record) =>
        !record.Sender.Equals("Me", StringComparison.OrdinalIgnoreCase) &&
        !record.ConversationId.Equals("system", StringComparison.OrdinalIgnoreCase) &&
        record.Text.Trim().Equals("typing", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyApplicationSender(ChatRecord record) =>
        record.Sender.Equals("FM", StringComparison.OrdinalIgnoreCase) &&
        (record.ConversationId.Equals("system", StringComparison.OrdinalIgnoreCase) ||
         record.Text.StartsWith("Unable to join group chat", StringComparison.OrdinalIgnoreCase) ||
         record.Text.StartsWith("The message could not be sent", StringComparison.OrdinalIgnoreCase));

    private void WireSecondLifeEvents()
    {
        _service.MessageReceived += record => BeginInvoke(() =>
        {
            var currentlyReading = IsReadingConversation(record.ConversationId);
            ReopenConversation(record.ConversationId);
            AddMessage(record);
            var source = record.ConversationId.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
                ? $"group {record.ConversationName}"
                : $"private IM with {record.Sender}";
            AddNotification($"New message in {source} from {record.Sender}.");
            if (!currentlyReading) IncrementUnread();
            RefreshAll();
            if (currentlyReading) SelectConversation(record.ConversationId);
        });
        _service.FriendsChanged += () => BeginInvoke(RefreshAll);
        _service.GroupsChanged += () => BeginInvoke(RefreshAll);
        _service.Status += text => BeginInvoke(() =>
        {
            AddSystem(text);
            AddNotification(text);
            RefreshAll();
        });
    }

    private void BuildShell()
    {
        var windowLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Bg
        };
        windowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        windowLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        windowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(windowLayout);

        BuildTitleBar();
        windowLayout.Controls.Add(_titleBar, 0, 0);

        _clientArea.Dock = DockStyle.Fill;
        _clientArea.Margin = Padding.Empty;
        _clientArea.BackColor = Theme.Bg;
        windowLayout.Controls.Add(_clientArea, 0, 1);

        _shell.Dock = DockStyle.Fill;
        _shell.ColumnCount = 2;
        _shell.RowCount = 1;
        _shell.Margin = Padding.Empty;
        _shell.Padding = Padding.Empty;
        _shell.BackColor = Theme.Bg;
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _clientArea.Controls.Add(_shell);

        var rail = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Rail, Padding = new Padding(12), Margin = Padding.Empty };
        _shell.Controls.Add(rail, 0, 0);

        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;
        _content.Margin = Padding.Empty;
        _shell.Controls.Add(_content, 1, 0);

        var railLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Rail
        };
        railLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        railLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        railLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rail.Controls.Add(railLayout);

        var brand = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Rail, Margin = Padding.Empty };
        railLayout.Controls.Add(brand, 0, 0);
        var brandImage = LoadImageCopy(Store.IconPath);
        if (brandImage is not null)
        {
            var logo = new PictureBox { Image = brandImage, SizeMode = PictureBoxSizeMode.Zoom };
            logo.SetBounds(4, 12, 52, 52);
            brand.Controls.Add(logo);
        }
        brand.Controls.Add(ShellLabel("Flair Messenger", 66, 15, 146, 24, 10, true));
        brand.Controls.Add(ShellLabel(AppInfo.Tagline, 66, 40, 160, 20, 9));

        var navigation = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 0,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Rail
        };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        railLayout.Controls.Add(navigation, 0, 1);

        foreach (var (text, action) in new (string, Action)[]
        {
            ("Chats", ShowChats),
            ("Friends", ShowFriends),
            ("Groups", ShowGroups),
            ("Notifications", ShowNotifications),
            ("Settings", ShowSettings),
            ("About", ShowAbout)
        })
        {
            var button = NavButton(text);
            button.Click += (_, _) =>
            {
                SetActiveNavigation(text);
                action();
            };
            navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            navigation.Controls.Add(button, 0, navigation.RowCount++);
            _navButtons[text] = button;
        }
        SetActiveNavigation("Chats");
        BuildLogoutOverlay();
    }

    private void BuildLogoutOverlay()
    {
        _logoutOverlay.Dock = DockStyle.Fill;
        _logoutOverlay.Margin = Padding.Empty;
        _logoutOverlay.BackColor = Theme.Bg;
        _logoutOverlay.Visible = false;
        _logoutOverlay.AccessibleName = "Inline signing out status";

        var centeringLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Bg
        };
        centeringLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        centeringLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 520));
        centeringLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        centeringLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        centeringLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        centeringLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _logoutOverlay.Controls.Add(centeringLayout);

        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(28, 24, 28, 24),
            BackColor = Theme.Panel
        };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        centeringLayout.Controls.Add(statusLayout, 1, 1);

        var title = new Label
        {
            Text = "Signing out of Second Life...",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft
        };
        statusLayout.Controls.Add(title, 0, 0);

        var explanation = new Label
        {
            Text = "Closing your avatar session safely. Please wait.",
            Dock = DockStyle.Fill,
            Font = Theme.Font,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft
        };
        statusLayout.Controls.Add(explanation, 0, 1);

        _logoutProgress.Dock = DockStyle.Fill;
        _logoutProgress.Style = ProgressBarStyle.Marquee;
        _logoutProgress.MarqueeAnimationSpeed = 25;
        _logoutProgress.Margin = Padding.Empty;
        _logoutProgress.AccessibleName = "Signing out progress";
        statusLayout.Controls.Add(_logoutProgress, 0, 2);

        _clientArea.Controls.Add(_logoutOverlay);
    }

    private void BuildTitleBar()
    {
        _titleBar.Dock = DockStyle.Fill;
        _titleBar.Margin = Padding.Empty;
        _titleBar.BackColor = Theme.Rail;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Rail
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _titleBar.Controls.Add(layout);

        var titleBarImage = LoadImageCopy(Store.IconPath);

        var iconHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 8, 8),
            Margin = Padding.Empty,
            BackColor = Theme.Rail
        };
        var icon = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = titleBarImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = Padding.Empty,
            BackColor = Theme.Rail
        };
        iconHost.Controls.Add(icon);
        layout.Controls.Add(iconHost, 0, 0);

        _windowTitle.Text = $"{AppInfo.ProductTitle} - v{AppInfo.Version}";
        _windowTitle.Dock = DockStyle.Fill;
        _windowTitle.Margin = new Padding(0, 9, 0, 9);
        _windowTitle.Padding = new Padding(2, 0, 0, 0);
        _windowTitle.AutoSize = false;
        _windowTitle.AutoEllipsis = true;
        _windowTitle.Font = new Font("Segoe UI", 9);
        _windowTitle.ForeColor = Theme.Muted;
        _windowTitle.BackColor = Theme.Rail;
        _windowTitle.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_windowTitle, 1, 0);

        var windowButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Rail
        };
        for (var i = 0; i < 3; i++) windowButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        windowButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(windowButtons, 2, 0);

        var minimize = WindowButton("—", "Minimize");
        _maximizeWindowButton.Text = "□";
        StyleWindowButton(_maximizeWindowButton, "Maximize");
        var close = WindowButton("×", "Close");
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        close.FlatAppearance.MouseDownBackColor = Color.FromArgb(160, 32, 24);

        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximizeWindowButton.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) => Close();
        windowButtons.Controls.Add(minimize, 0, 0);
        windowButtons.Controls.Add(_maximizeWindowButton, 1, 0);
        windowButtons.Controls.Add(close, 2, 0);

        foreach (var dragSurface in new Control[] { _titleBar, layout, iconHost, icon, _windowTitle })
        {
            dragSurface.MouseDown += BeginWindowDrag;
            dragSurface.DoubleClick += (_, _) => ToggleMaximize();
        }
    }

    private static Image? LoadImageCopy(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var source = Image.FromFile(path);
            return new Bitmap(source);
        }
        catch
        {
            return null;
        }
    }

    private static Button WindowButton(string text, string accessibleName)
    {
        var button = accessibleName == "Close" ? new WindowCloseButton() : new Button();
        if (accessibleName == "Close") text = "";
        button.Text = text;
        StyleWindowButton(button, accessibleName);
        return button;
    }

    private static void StyleWindowButton(Button button, string accessibleName)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = Padding.Empty;
        button.Padding = Padding.Empty;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Input;
        button.FlatAppearance.MouseDownBackColor = Theme.Panel;
        button.BackColor = Theme.Rail;
        button.ForeColor = Theme.Text;
        button.Font = Theme.WindowGlyph;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.MinimumSize = accessibleName == "Close" ? new Size(44, 32) : Size.Empty;
        button.TabStop = false;
        button.UseVisualStyleBackColor = false;
        button.AccessibleName = accessibleName;
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        _maximizeWindowButton.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
        _maximizeWindowButton.AccessibleName = WindowState == FormWindowState.Maximized ? "Restore" : "Maximize";
    }

    private void ShowChats()
    {
        // Detach before removing the previous page. Destroying and recreating the
        // ListBox handle can otherwise publish its stale selection and overwrite
        // a friend or group that was just opened.
        _conversations.SelectedIndexChanged -= ConversationChanged;
        _conversations = new ListBox();
        SetActiveNavigation("Chats");
        ClearUnread();
        _content.Controls.Clear();
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 1,
            BackColor = Theme.Bg
        };
        _content.Controls.Add(split);
        split.HandleCreated += (_, _) => SetConversationSplitter(split);
        split.SizeChanged += (_, _) => SetConversationSplitter(split);

        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(14) };
        split.Panel1.Controls.Add(left);
        var conversationsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Panel
        };
        conversationsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        conversationsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        conversationsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(conversationsLayout);
        var conversationsTitle = ShellLabel("Conversations", 0, 0, 210, 28, 12, true);
        conversationsTitle.Dock = DockStyle.Fill;
        conversationsLayout.Controls.Add(conversationsTitle, 0, 0);

        _conversations.Dock = DockStyle.Fill;
        _conversations.Margin = Padding.Empty;
        StyleList(_conversations);
        _conversations.SelectedIndexChanged += ConversationChanged;
        conversationsLayout.Controls.Add(_conversations, 0, 1);

        var chatLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty, Padding = Padding.Empty };
        chatLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        chatLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        split.Panel2.Controls.Add(chatLayout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(18, 8, 18, 8),
            BackColor = Theme.Panel
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var headerText = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Panel };
        _title.Text = _active.Name;
        _title.Dock = DockStyle.Top;
        _title.Height = 28;
        _title.Font = new Font("Segoe UI", 14, FontStyle.Bold);
        _title.ForeColor = Theme.Text;
        _title.BackColor = Color.Transparent;
        _subtitle.Text = ConversationSubtitle(_active);
        _subtitle.Dock = DockStyle.Top;
        _subtitle.Height = 22;
        _subtitle.Font = new Font("Segoe UI", 9);
        _subtitle.ForeColor = Theme.Muted;
        _subtitle.BackColor = Color.Transparent;
        headerText.Controls.Add(_subtitle);
        headerText.Controls.Add(_title);
        header.Controls.Add(headerText, 0, 0);

        _closeChatButton.Dock = DockStyle.Fill;
        _closeChatButton.Margin = new Padding(8, 4, 0, 4);
        _closeChatButton.Visible = _active.Kind != ConversationKind.System;
        header.Controls.Add(_closeChatButton, 1, 0);
        chatLayout.Controls.Add(header, 0, 0);

        var feedPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 12, 18, 8), Margin = Padding.Empty };
        _messageFeed.Dock = DockStyle.Fill;
        _messageFeed.Margin = Padding.Empty;
        _messageFeed.ReadOnly = true;
        _messageFeed.BorderStyle = BorderStyle.None;
        _messageFeed.BackColor = Theme.Bg;
        _messageFeed.ForeColor = Theme.Text;
        _messageFeed.Font = new Font("Segoe UI", 10);
        feedPanel.Controls.Add(_messageFeed);
        chatLayout.Controls.Add(feedPanel, 0, 1);

        var composer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 14, 18, 14) };
        var composerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        composerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        composerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var input = LoginForm.Box();
        input.Multiline = false;
        input.Dock = DockStyle.Fill;
        input.AutoSize = false;
        input.Margin = Padding.Empty;
        var send = LoginForm.Button("Send");
        send.Dock = DockStyle.Fill;
        send.Margin = new Padding(8, 0, 0, 0);
        send.Click += (_, _) => SendActive(input, send);
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendActive(input, send);
            }
        };
        composerLayout.Controls.Add(input, 0, 0);
        composerLayout.Controls.Add(send, 1, 0);
        composer.Controls.Add(composerLayout);
        chatLayout.Controls.Add(composer, 0, 2);

        RefreshConversations();
        RenderMessages();
    }

    private static void SetConversationSplitter(SplitContainer split)
    {
        var min = split.Panel1MinSize;
        var max = split.ClientSize.Width - split.Panel2MinSize - split.SplitterWidth;
        if (max < min) return;

        var target = Math.Clamp(260, min, max);
        if (split.SplitterDistance != target)
            split.SplitterDistance = target;
    }

    private void ShowFriends()
    {
        _friendsList = new ListBox();
        SetActiveNavigation("Friends");
        ShowListPage("Friends", _friendsList, () =>
        {
            _friendsList.Items.Clear();
            foreach (var friend in _service.Friends.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Name))
            {
                var name = string.IsNullOrWhiteSpace(friend.Name) ? $"Resolving name ({friend.UUID})" : friend.Name;
                var displayText = $"{name} - {(friend.IsOnline ? "Online" : "Offline")}";
                _friendsList.Items.Add(new ConversationItem(friend.UUID.ToString(), name, ConversationKind.Private, DisplayText: displayText));
            }
            if (_friendsList.Items.Count == 0) _friendsList.Items.Add(_service.FriendsLoadStatus);
        }, () => _ = RefreshFriendsPageAsync(waitForLoginData: true));
        _friendsList.DoubleClick -= OpenSelectedFriend;
        _friendsList.DoubleClick += OpenSelectedFriend;
        _friendsList.KeyDown -= OpenSelectedFriendFromKeyboard;
        _friendsList.KeyDown += OpenSelectedFriendFromKeyboard;

        if (!_friendsAutoRefreshAttempted)
        {
            _friendsAutoRefreshAttempted = true;
            _ = RefreshFriendsPageAsync(waitForLoginData: true);
        }
    }

    private async Task RefreshFriendsPageAsync(bool waitForLoginData)
    {
        if (_friendsRefreshRunning) return;
        _friendsRefreshRunning = true;
        try
        {
            await _service.RefreshFriendsAsync(CancellationToken.None, waitForLoginData);
        }
        finally
        {
            _friendsRefreshRunning = false;
            if (!IsDisposed)
            {
                BeginInvoke(() =>
                {
                    // The user may have opened a conversation after this refresh completed
                    // but before the queued UI callback runs. Never switch them back to Friends.
                    if (!IsDisposed && _activePage.Equals("Friends", StringComparison.OrdinalIgnoreCase))
                        ShowFriends();
                });
            }
        }
    }

    private void ShowGroups()
    {
        _groupsList = new ListBox();
        SetActiveNavigation("Groups");
        ShowListPage("Groups", _groupsList, () =>
        {
            _groupsList.Items.Clear();
            foreach (var group in _service.Groups.Values.OrderBy(g => g.Name))
            {
                var displayText = $"{group.Name} - {group.GroupMembershipCount} members";
                _groupsList.Items.Add(new ConversationItem($"group:{group.ID}", group.Name, ConversationKind.Group, DisplayText: displayText));
            }
            if (_groupsList.Items.Count == 0) _groupsList.Items.Add("No groups loaded.");
        });
        _groupsList.DoubleClick -= OpenSelectedGroup;
        _groupsList.DoubleClick += OpenSelectedGroup;
        _groupsList.KeyDown -= OpenSelectedGroupFromKeyboard;
        _groupsList.KeyDown += OpenSelectedGroupFromKeyboard;
    }

    private void ShowNotifications()
    {
        SetActiveNavigation("Notifications");
        var list = new ListBox();
        ShowListPage("Notifications", list, () =>
        {
            foreach (var item in _notifications.OrderByDescending(n => n.Time))
                list.Items.Add($"[{item.Time:HH:mm}] {item.Text}");
            if (list.Items.Count == 0) list.Items.Add("No notifications.");
        });
    }

    private void ShowSettings()
    {
        SetActiveNavigation("Settings");
        _content.Controls.Clear();

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(26) };
        _content.Controls.Add(panel);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Bg
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(layout);

        var heading = ShellLabel("Settings", 0, 0, 500, 36, 18, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);

        var preferencePanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(18, 14, 18, 12),
            BackColor = Theme.Panel
        };
        layout.Controls.Add(preferencePanel, 0, 1);

        var minimizeToTray = new CheckBox
        {
            Text = "Minimize to system tray",
            Checked = _minimizeToTray,
            AutoSize = false,
            Height = 30,
            Dock = DockStyle.Top,
            Font = Theme.Bold,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var explanation = ShellLabel(
            "When enabled, minimizing hides Flair Messenger in the notification area. " +
            "When disabled, the client remains on the Windows taskbar.",
            0, 0, 700, 46, 9);
        explanation.Dock = DockStyle.Top;
        explanation.ForeColor = Theme.Muted;
        explanation.Padding = new Padding(22, 0, 0, 0);
        var savedStatus = ShellLabel("", 0, 0, 700, 24, 9);
        savedStatus.Dock = DockStyle.Top;
        savedStatus.ForeColor = Theme.Muted;
        savedStatus.Padding = new Padding(22, 0, 0, 0);
        minimizeToTray.CheckedChanged += (_, _) =>
        {
            _minimizeToTray = minimizeToTray.Checked;
            if (!_minimizeToTray) _tray.Visible = false;
            try
            {
                Store.SaveClientPreferences(_minimizeToTray);
                savedStatus.Text = "Saved.";
            }
            catch
            {
                savedStatus.Text = "The setting could not be saved, but it remains active for this session.";
            }
        };
        preferencePanel.Controls.Add(savedStatus);
        preferencePanel.Controls.Add(explanation);
        preferencePanel.Controls.Add(minimizeToTray);

        var details = new ListBox { Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        StyleList(details);
        details.Items.Add($"Login name: {_loginName}");
        details.Items.Add($"Login location: {LoginLocationParser.Display(_location)}");
        details.Items.Add("Use Remember details on the login screen to save account profiles.");
        details.Items.Add("Chats are stored locally in the data folder next to the BAT file.");
        layout.Controls.Add(details, 0, 2);
    }

    private void ShowAbout()
    {
        SetActiveNavigation("About");
        ShowTextPage("About", new[]
        {
            AppInfo.ProductTitle,
            "Second Life login and IM through LibreMetaverse.",
            "Private IM, Friends, Groups, Notifications, Settings and tray mode.",
            $"Version {AppInfo.Version}"
        });
    }

    private void ShowListPage(string title, ListBox list, Action fill, Action? refresh = null)
    {
        _content.Controls.Clear();
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(26) };
        _content.Controls.Add(panel);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Theme.Bg };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(layout);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty };
        var label = ShellLabel(title, 0, 0, 500, 36, 18, true);
        label.Dock = DockStyle.Fill;
        header.Controls.Add(label);
        if (refresh is not null)
        {
            var refreshButton = LoginForm.Button("Refresh");
            refreshButton.Dock = DockStyle.Right;
            refreshButton.Width = 94;
            refreshButton.Margin = Padding.Empty;
            refreshButton.Click += (_, _) => refresh();
            header.Controls.Add(refreshButton);
            refreshButton.BringToFront();
        }
        layout.Controls.Add(header, 0, 0);

        list.Dock = DockStyle.Fill;
        list.Margin = Padding.Empty;
        StyleList(list);
        layout.Controls.Add(list, 0, 1);
        fill();
    }

    private void ShowTextPage(string title, IEnumerable<string> lines)
    {
        var list = new ListBox();
        ShowListPage(title, list, () =>
        {
            foreach (var line in lines) list.Items.Add(line);
        });
    }

    private void ConversationChanged(object? sender, EventArgs e)
    {
        if (_conversations.SelectedItem is ConversationItem item)
        {
            _active = item;
            UpdateConversationHeader(item);
            RenderMessages();
            if (!_refreshingConversations) ClearUnread();
        }
    }

    private void OpenSelectedFriend(object? sender, EventArgs e)
    {
        if (_friendsList.SelectedItem is ConversationItem item)
            OpenConversation(item);
    }

    private void OpenSelectedGroup(object? sender, EventArgs e)
    {
        if (_groupsList.SelectedItem is ConversationItem item)
            OpenConversation(item);
    }

    private void OpenSelectedFriendFromKeyboard(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        OpenSelectedFriend(sender, EventArgs.Empty);
    }

    private void OpenSelectedGroupFromKeyboard(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        OpenSelectedGroup(sender, EventArgs.Empty);
    }

    private void OpenConversation(ConversationItem item)
    {
        ReopenConversation(item.Id);
        _active = item with { ShowSource = true, DisplayText = null };
        ShowChats();
        SelectConversation(_active.Id);
    }

    private void CloseActiveConversation()
    {
        if (_active.Kind == ConversationKind.System) return;
        _closedConversationIds.Add(_active.Id);
        _conversationHistoryCutoffs[_active.Id] = DateTime.UtcNow;
        PersistClosedConversations();
        _active = new ConversationItem("system", "System", ConversationKind.System);
        RefreshConversations();
    }

    private void ReopenConversation(string conversationId)
    {
        if (!_closedConversationIds.Remove(conversationId)) return;
        PersistClosedConversations();
    }

    private void PersistClosedConversations()
    {
        try
        {
            Store.WriteConversationState(_loginName, _closedConversationIds, _conversationHistoryCutoffs);
        }
        catch
        {
            // Closing still works for this session when encrypted settings cannot be updated.
        }
    }

    private async void SendActive(TextBox input, Button send)
    {
        if (_sendingMessage) return;
        var text = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var conversation = _active;

        _sendingMessage = true;
        input.Enabled = false;
        send.Enabled = false;
        send.Text = conversation.Kind == ConversationKind.Group ? "Joining..." : "Sending...";

        bool sent;
        string failureMessage;
        try
        {
            if (conversation.Kind == ConversationKind.Group)
            {
                var result = await _service.SendGroupMessageAsync(conversation.Id.Replace("group:", ""), text, CancellationToken.None);
                sent = result.Success;
                failureMessage = result.Message;
            }
            else
            {
                sent = conversation.Kind == ConversationKind.Private && _service.SendInstantMessage(conversation.Id, text);
                failureMessage = "Select a friend or group before sending a message.";
            }
        }
        catch (Exception ex)
        {
            sent = false;
            failureMessage = $"The message could not be sent: {ex.Message}";
        }
        finally
        {
            _sendingMessage = false;
            input.Enabled = true;
            send.Enabled = true;
            send.Text = "Send";
            input.Focus();
        }

        if (!sent)
        {
            if (conversation.Kind == ConversationKind.Group)
            {
                AddMessage(new ChatRecord { ConversationId = conversation.Id, ConversationName = conversation.Name, Sender = AppInfo.Name, Text = failureMessage, Time = DateTime.Now });
                if (_active.Id == conversation.Id) RenderMessages();
            }
            else
            {
                AddSystem(failureMessage);
                SelectConversation("system");
            }
            RefreshConversations();
            return;
        }

        AddMessage(new ChatRecord { ConversationId = conversation.Id, ConversationName = conversation.Name, Sender = "Me", Text = text, Time = DateTime.Now });
        input.Clear();
        RefreshConversations();
        if (_active.Id == conversation.Id)
        {
            SelectConversation(conversation.Id);
            RenderMessages();
        }
    }

    private void RefreshAll()
    {
        RefreshConversations();
        if (_activePage.Equals("Friends", StringComparison.OrdinalIgnoreCase)) ShowFriends();
        if (_activePage.Equals("Groups", StringComparison.OrdinalIgnoreCase)) ShowGroups();
    }

    private void RefreshConversations()
    {
        _refreshingConversations = true;
        try
        {
            var selected = _active.Id;
            _conversations.Items.Clear();
            _conversations.Items.Add(new ConversationItem("system", "System", ConversationKind.System));

            var nowUtc = DateTime.UtcNow;
            var recentConversations = _messages
                .Where(message => IsRecentConversationMessage(message, nowUtc) &&
                    !_closedConversationIds.Contains(message.ConversationId))
                .GroupBy(message => message.ConversationId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(message => AsUtc(message.Time)).First())
                .OrderByDescending(message => AsUtc(message.Time));

            foreach (var message in recentConversations)
            {
                var kind = message.ConversationId.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
                    ? ConversationKind.Group
                    : ConversationKind.Private;
                var name = kind == ConversationKind.Group
                    ? _service.ResolveGroupConversationName(message.ConversationId, message.ConversationName)
                    : message.ConversationName;
                _conversations.Items.Add(new ConversationItem(message.ConversationId, name, kind, true));
            }

            // Keep a conversation that the user deliberately opened from Friends or Groups,
            // even when it does not have a message in the recent 24-hour window yet.
            if (!_active.Id.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                _conversations.Items.Cast<ConversationItem>().All(item =>
                    !item.Id.Equals(_active.Id, StringComparison.OrdinalIgnoreCase)))
                _conversations.Items.Add(_active with { ShowSource = true });

            SelectConversation(selected);
        }
        finally
        {
            _refreshingConversations = false;
        }
    }

    private static bool IsRecentConversationMessage(ChatRecord message, DateTime nowUtc) =>
        !message.ConversationId.Equals("system", StringComparison.OrdinalIgnoreCase) &&
        AsUtc(message.Time) >= nowUtc.Subtract(RecentConversationWindow);

    private static DateTime AsUtc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Local).ToUniversalTime()
    };

    private void SelectConversation(string id)
    {
        for (var i = 0; i < _conversations.Items.Count; i++)
        {
            if (_conversations.Items[i] is ConversationItem item && item.Id == id)
            {
                _conversations.SelectedIndex = i;
                _active = item;
                UpdateConversationHeader(item);
                RenderMessages();
                return;
            }
        }
        if (_conversations.Items.Count > 0)
        {
            _conversations.SelectedIndex = 0;
            _active = (ConversationItem)_conversations.Items[0];
            UpdateConversationHeader(_active);
            RenderMessages();
        }
    }

    private void UpdateConversationHeader(ConversationItem item)
    {
        _title.Text = item.Name;
        _subtitle.Text = ConversationSubtitle(item);
        _closeChatButton.Visible = item.Kind != ConversationKind.System;
    }

    private string ConversationSubtitle(ConversationItem item) => item.Kind switch
    {
        ConversationKind.Group => "Group chat - messages from group members",
        ConversationKind.Private => "Private instant message",
        _ => $"{_loginName} - {LoginLocationParser.Display(_location)}" 
    };

    private void RenderMessages()
    {
        _messageFeed.Clear();
        foreach (var msg in _messages.Where(m => m.ConversationId == _active.Id).OrderBy(m => m.Time))
        {
            var historyCutoff = _conversationHistoryCutoffs.TryGetValue(msg.ConversationId, out var closedAt)
                ? AsUtc(closedAt)
                : _sessionStartedUtc;
            _messageFeed.SelectionColor = AsUtc(msg.Time) <= historyCutoff
                ? Theme.HistoryText
                : Theme.Text;
            _messageFeed.AppendText($"[{msg.Time:HH:mm}] {msg.Sender}: {msg.Text}{Environment.NewLine}{Environment.NewLine}");
        }
        _messageFeed.SelectionColor = Theme.Text;
        _messageFeed.SelectionStart = _messageFeed.TextLength;
        _messageFeed.ScrollToCaret();
    }

    private void AddSystem(string text) => AddMessage(new ChatRecord { ConversationId = "system", ConversationName = "System", Sender = AppInfo.Name, Text = text, Time = DateTime.Now });

    private void AddNotification(string text) => _notifications.Add(new ChatRecord { ConversationId = "notifications", ConversationName = "Notifications", Sender = AppInfo.Name, Text = text, Time = DateTime.Now });

    private void AddMessage(ChatRecord record)
    {
        _messages.Add(record);
        Store.WriteMessages(_messages);
    }

    private bool IsReadingConversation(string conversationId) =>
        Visible && WindowState != FormWindowState.Minimized && ContainsFocus &&
        _activePage.Equals("Chats", StringComparison.OrdinalIgnoreCase) &&
        _active.Id.Equals(conversationId, StringComparison.OrdinalIgnoreCase);

    private void IncrementUnread()
    {
        if (_unreadCount < 999) _unreadCount++;
        UpdateUnreadBadge();
    }

    private void ClearUnread()
    {
        if (_unreadCount == 0 && _unreadBadgeIcon is null) return;
        _unreadCount = 0;
        UpdateUnreadBadge();
    }

    private void UpdateUnreadBadge()
    {
        var oldBadge = _unreadBadgeIcon;
        _unreadBadgeIcon = _unreadCount > 0 ? CreateUnreadBadgeIcon(_unreadCount) : null;
        var displayIcon = _unreadBadgeIcon ?? _baseWindowIcon;
        Icon = displayIcon;
        _tray.Icon = displayIcon;
        _tray.Text = _unreadCount > 0
            ? $"Flair Messenger - {_unreadCount} unread message{(_unreadCount == 1 ? "" : "s")}"
            : "Flair Messenger";
        oldBadge?.Dispose();
    }

    private Icon CreateUnreadBadgeIcon(int count)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var baseImage = _baseWindowIcon.ToBitmap())
            graphics.DrawImage(baseImage, new Rectangle(0, 0, 32, 32));

        var badgeBounds = new RectangleF(15, 15, 17, 17);
        using (var badgeBrush = new SolidBrush(Color.FromArgb(237, 66, 69)))
            graphics.FillEllipse(badgeBrush, badgeBounds);
        using (var outline = new Pen(Color.White, 1.6f))
            graphics.DrawEllipse(outline, badgeBounds);

        var badgeText = count > 99 ? "99+" : count.ToString();
        using var font = new Font("Segoe UI", badgeText.Length > 2 ? 6.2f : 8f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(badgeText, font, textBrush, badgeBounds, format);

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowedIcon = Icon.FromHandle(handle);
            return (Icon)borrowedIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private void SetActiveNavigation(string page)
    {
        _activePage = page;
        foreach (var (name, button) in _navButtons)
        {
            var selected = name.Equals(page, StringComparison.OrdinalIgnoreCase);
            button.BackColor = selected ? Theme.Accent : Theme.Rail;
            button.ForeColor = selected ? Color.White : Theme.Muted;
            button.Font = selected ? Theme.Bold : Theme.Font;
        }
    }

    private static Button NavButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = 46,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Rail,
            ForeColor = Theme.Muted,
            Font = Theme.Font,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            Margin = Padding.Empty,
            TabStop = false,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Input;
        button.FlatAppearance.MouseDownBackColor = Theme.Accent;
        return button;
    }

    private static Label ShellLabel(string text, int x, int y, int w, int h, float size = 10, bool bold = false)
    {
        var label = LoginForm.Label(text, x, y, w, h, size, bold);
        label.AutoEllipsis = true;
        return label;
    }

    private static void StyleList(ListBox list)
    {
        list.BackColor = Theme.Panel;
        list.ForeColor = Theme.Text;
        list.BorderStyle = BorderStyle.None;
        list.Font = Theme.Font;
        list.IntegralHeight = false;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        UpdateMaximizeButton();
        Activate();
        _tray.Visible = false;
        if (_activePage.Equals("Chats", StringComparison.OrdinalIgnoreCase)) ClearUnread();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmNcHitTest || WindowState != FormWindowState.Normal || m.Result != (IntPtr)HtClient) return;

        const int grip = 8;
        var packed = m.LParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
        var point = PointToClient(screenPoint);
        var left = point.X <= grip;
        var right = point.X >= ClientSize.Width - grip;
        var top = point.Y <= grip;
        var bottom = point.Y >= ClientSize.Height - grip;

        if (left && top) m.Result = (IntPtr)HtTopLeft;
        else if (right && top) m.Result = (IntPtr)HtTopRight;
        else if (left && bottom) m.Result = (IntPtr)HtBottomLeft;
        else if (right && bottom) m.Result = (IntPtr)HtBottomRight;
        else if (left) m.Result = (IntPtr)HtLeft;
        else if (right) m.Result = (IntPtr)HtRight;
        else if (top) m.Result = (IntPtr)HtTop;
        else if (bottom) m.Result = (IntPtr)HtBottom;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateMaximizeButton();
        if (WindowState == FormWindowState.Minimized && _minimizeToTray)
        {
            Hide();
            _tray.Visible = true;
            _tray.ShowBalloonTip(1200, AppInfo.Name, "Flair Messenger is still running in the system tray.", ToolTipIcon.Info);
        }
        else if (WindowState == FormWindowState.Minimized)
        {
            _tray.Visible = false;
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_logoutFinished && _service.IsLoggedIn)
        {
            e.Cancel = true;
            base.OnFormClosing(e);

            if (_logoutStarted) return;
            _logoutStarted = true;
            Text = "Flair Messenger - Signing out...";
            _windowTitle.Text = "Flair Messenger - Signing out...";
            _tray.Visible = false;
            ShowLogoutOverlay();
            var minimumVisibleTime = Task.Delay(TimeSpan.FromMilliseconds(750));

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await _service.LogoutAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Dispose performs a blocking logout fallback if the connection is still active.
            }
            catch (Exception)
            {
                // Dispose performs the same fallback if the graceful request fails unexpectedly.
            }
            finally
            {
                await minimumVisibleTime;
                _logoutProgress.MarqueeAnimationSpeed = 0;
                _logoutFinished = true;
                Close();
            }
            return;
        }

        _tray.Visible = false;
        _tray.Icon = _baseWindowIcon;
        _tray.Dispose();
        Icon = _baseWindowIcon;
        _unreadBadgeIcon?.Dispose();
        _unreadBadgeIcon = null;
        _service.Dispose();
        base.OnFormClosing(e);
    }

    private void ShowLogoutOverlay()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        UpdateMaximizeButton();
        _shell.Enabled = false;
        _logoutProgress.MarqueeAnimationSpeed = 25;
        _logoutOverlay.Visible = true;
        _logoutOverlay.BringToFront();
        Activate();
    }

    private sealed record ConversationItem(
        string Id,
        string Name,
        ConversationKind Kind,
        bool ShowSource = false,
        string? DisplayText = null)
    {
        public override string ToString() => DisplayText ?? (ShowSource
            ? Kind switch
            {
                ConversationKind.Group => $"Group: {Name}",
                ConversationKind.Private => $"Private IM: {Name}",
                _ => Name
            }
            : Name);
    }

    private enum ConversationKind
    {
        System,
        Private,
        Group
    }
}
