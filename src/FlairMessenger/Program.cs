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
    public const string Version = "0.4.14";
    public const string Name = "Flair Messenger";
    public const string UserAgent = Name + " " + Version;
}

internal sealed class AppSettings
{
    public bool Remember { get; set; }
    public string LoginName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Location { get; set; } = "last";
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
            return ReadProtected(SettingsPath, new AppSettings());

        var settings = ReadJson(LegacySettingsPath, new AppSettings());
        if (!File.Exists(LegacySettingsPath)) return settings;

        settings.Password = UnprotectLegacyPassword(settings.Password);
        TryMigrate(LegacySettingsPath, SettingsPath, settings);
        return settings;
    }

    public static void WriteSettings(AppSettings settings) => WriteProtected(SettingsPath, settings);

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
    public static readonly Font Font = new("Segoe UI", 10);
    public static readonly Font Bold = new("Segoe UI", 10, FontStyle.Bold);
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
        var parts = SplitLoginName(loginName);
        Status?.Invoke("Signing in to Second Life...");
        var loginParams = _client.Network.DefaultLoginParams(parts.First, parts.Last, password, AppInfo.Name, AppInfo.Version);
        loginParams.Start = start.Equals("home", StringComparison.OrdinalIgnoreCase) ? "home" : "last";
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

    private const string SecondLifeTermsUrl = "https://lindenlab.com/legal/second-life-terms-and-conditions";
    private const string ThirdPartyViewerPolicyUrl = "https://secondlife.com/corporate/third-party-viewers";
    private const string LindenPrivacyUrl = "https://lindenlab.com/privacy";

    private readonly TextBox _login = Box();
    private readonly TextBox _password = Box();
    private readonly ComboBox _location = new();
    private readonly CheckBox _remember = new();
    private readonly Label _error = Label("");
    private readonly Button _loginButton = Button("Login");
    private readonly CheckBox _termsAccepted = new();
    private readonly LinkLabel _policyLinks = new();
    private readonly ProgressBar _loginProgress = new()
    {
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 25,
        Visible = false
    };

    public LoginForm()
    {
        Text = "Flair Messenger Login";
        Size = new Size(440, 525);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Icon = AppIcon();

        var settings = Store.ReadSettings();
        if (settings.Remember)
        {
            _login.Text = settings.LoginName;
            _password.Text = settings.Password;
            _remember.Checked = true;
        }

        AddHeader(this);
        Add(Label("Login name:", 32, 112), _login, 32, 138, 350);
        Add(Label("Password:", 32, 180), _password, 32, 206, 350);
        _password.UseSystemPasswordChar = true;

        Controls.Add(Label("Login location:", 32, 248));
        _location.SetBounds(32, 274, 350, 30);
        _location.DropDownStyle = ComboBoxStyle.DropDownList;
        _location.BackColor = Theme.Input;
        _location.ForeColor = Theme.Text;
        _location.Items.AddRange(["Home", "Last location"]);
        _location.SelectedIndex = settings.Location == "home" ? 0 : 1;
        Controls.Add(_location);

        _remember.Text = "Remember details";
        _remember.SetBounds(32, 316, 180, 28);
        _remember.ForeColor = Theme.Muted;
        _remember.BackColor = Theme.Bg;
        Controls.Add(_remember);

        _loginButton.SetBounds(246, 316, 136, 36);
        _loginButton.Enabled = false;
        _loginButton.Click += LoginClicked;
        Controls.Add(_loginButton);

        _termsAccepted.Text = "I accept the current Second Life terms and policies.";
        _termsAccepted.SetBounds(32, 358, 350, 28);
        _termsAccepted.ForeColor = Theme.Muted;
        _termsAccepted.BackColor = Theme.Bg;
        _termsAccepted.CheckedChanged += (_, _) => _loginButton.Enabled = _termsAccepted.Checked;
        Controls.Add(_termsAccepted);

        ConfigurePolicyLinks();
        _policyLinks.SetBounds(32, 390, 350, 24);
        Controls.Add(_policyLinks);

        _loginProgress.SetBounds(32, 425, 350, 8);
        Controls.Add(_loginProgress);

        _error.SetBounds(32, 441, 350, 36);
        _error.ForeColor = Color.FromArgb(248, 113, 113);
        Controls.Add(_error);
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

        _loginButton.Enabled = false;
        _loginProgress.Visible = true;
        _error.ForeColor = Theme.Muted;
        _error.Text = "Connecting to Second Life...";

        var settings = new AppSettings
        {
            Remember = _remember.Checked,
            LoginName = _remember.Checked ? _login.Text.Trim() : "",
            Password = _remember.Checked ? _password.Text : "",
            Location = _location.SelectedIndex == 0 ? "home" : "last"
        };
        Store.WriteSettings(settings);

        var service = new SecondLifeService();
        service.Status += UpdateLoginStatus;
        var result = await service.LoginAsync(_login.Text, _password.Text, settings.Location, CancellationToken.None);
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

        _error.Text = "Opening Flair Messenger...";
        Hide();
        var mainForm = new MainForm(service, _login.Text.Trim(), settings.Location);
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
        _policyLinks.LinkColor = Color.FromArgb(147, 197, 253);
        _policyLinks.ActiveLinkColor = Color.White;
        _policyLinks.VisitedLinkColor = _policyLinks.LinkColor;
        _policyLinks.BackColor = Theme.Bg;

        _policyLinks.Links.Add(0, 5, SecondLifeTermsUrl);
        _policyLinks.Links.Add(8, 25, ThirdPartyViewerPolicyUrl);
        _policyLinks.Links.Add(36, 7, LindenPrivacyUrl);
        _policyLinks.LinkClicked += (_, e) => OpenPolicyLink(e.Link?.LinkData as string);
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
        parent.Controls.Add(Label("Flair Messenger", 106, 30, 260, 30, 18, true));
        parent.Controls.Add(Label($"FM - Second Life | v{AppInfo.Version}", 108, 62, 220, 24, 10));
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

    internal static Button Button(string text) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Accent,
        ForeColor = Color.White,
        Font = Theme.Bold
    };

    internal static Label Label(string text, int x = 0, int y = 0, int w = 350, int h = 24, float size = 10, bool bold = false)
    {
        var label = new Label { Text = text, ForeColor = Theme.Text, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
        label.SetBounds(x, y, w, h);
        return label;
    }

    private void Add(Label label, TextBox box, int x, int y, int width)
    {
        Controls.Add(label);
        box.SetBounds(x, y, width, 30);
        Controls.Add(box);
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
    private readonly Panel _content = new();
    private readonly Panel _titleBar = new();
    private readonly Label _windowTitle = new();
    private readonly Button _maximizeWindowButton = new();
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Icon _baseWindowIcon;
    private readonly NotifyIcon _tray;
    private Icon? _unreadBadgeIcon;
    private static readonly TimeSpan RecentConversationWindow = TimeSpan.FromHours(24);
    private ConversationItem _active = new("system", "System", ConversationKind.System);
    private bool _logoutStarted;
    private bool _logoutFinished;
    private bool _friendsAutoRefreshAttempted;
    private bool _friendsRefreshRunning;
    private bool _sendingMessage;
    private bool _refreshingConversations;
    private string _activePage = "Chats";
    private int _unreadCount;

    public MainForm(SecondLifeService service, string loginName, string location)
    {
        _service = service;
        _loginName = loginName;
        _location = location;
        var storedMessages = Store.ReadMessages();
        _messages = storedMessages.Where(message => !IsLegacyTypingArtifact(message)).ToList();
        if (_messages.Count != storedMessages.Count) Store.WriteMessages(_messages);
        _notifications = new List<ChatRecord>();

        Text = $"Flair Messenger (FM) v{AppInfo.Version}";
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

    private void WireSecondLifeEvents()
    {
        _service.MessageReceived += record => BeginInvoke(() =>
        {
            var currentlyReading = IsReadingConversation(record.ConversationId);
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

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Bg
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        windowLayout.Controls.Add(shell, 0, 1);

        var rail = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Rail, Padding = new Padding(12), Margin = Padding.Empty };
        shell.Controls.Add(rail, 0, 0);

        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;
        _content.Margin = Padding.Empty;
        shell.Controls.Add(_content, 1, 0);

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
        brand.Controls.Add(ShellLabel("Second Life IM", 66, 40, 146, 20, 9));

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

        _windowTitle.Text = $"Flair Messenger (FM) - v{AppInfo.Version}";
        _windowTitle.Dock = DockStyle.Fill;
        _windowTitle.Margin = Padding.Empty;
        _windowTitle.Padding = new Padding(2, 0, 0, 0);
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
        var button = new Button();
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
        button.Font = new Font("Segoe UI Symbol", 11);
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

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(18, 12, 18, 8) };
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
        header.Controls.Add(_subtitle);
        header.Controls.Add(_title);
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
        ShowTextPage("Settings", new[]
        {
            $"Login name: {_loginName}",
            $"Login location: {(_location == "home" ? "Home" : "Last location")}",
            "Use Remember details on the login screen.",
            "Minimizing moves FM to the system tray.",
            "Chats are stored locally in the data folder next to the BAT file."
        });
    }

    private void ShowAbout()
    {
        SetActiveNavigation("About");
        ShowTextPage("About", new[]
        {
            "Flair Messenger (FM)",
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
        _active = item with { ShowSource = true, DisplayText = null };
        ShowChats();
        SelectConversation(_active.Id);
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
                AddMessage(new ChatRecord { ConversationId = conversation.Id, ConversationName = conversation.Name, Sender = "FM", Text = failureMessage, Time = DateTime.Now });
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
                .Where(message => IsRecentConversationMessage(message, nowUtc))
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
    }

    private string ConversationSubtitle(ConversationItem item) => item.Kind switch
    {
        ConversationKind.Group => "Group chat - messages from group members",
        ConversationKind.Private => "Private instant message",
        _ => $"{_loginName} - {(_location == "home" ? "Home" : "Last location")}" 
    };

    private void RenderMessages()
    {
        _messageFeed.Clear();
        foreach (var msg in _messages.Where(m => m.ConversationId == _active.Id).OrderBy(m => m.Time))
            _messageFeed.AppendText($"[{msg.Time:HH:mm}] {msg.Sender}: {msg.Text}{Environment.NewLine}{Environment.NewLine}");
        _messageFeed.SelectionStart = _messageFeed.TextLength;
        _messageFeed.ScrollToCaret();
    }

    private void AddSystem(string text) => AddMessage(new ChatRecord { ConversationId = "system", ConversationName = "System", Sender = "FM", Text = text, Time = DateTime.Now });

    private void AddNotification(string text) => _notifications.Add(new ChatRecord { ConversationId = "notifications", ConversationName = "Notifications", Sender = "FM", Text = text, Time = DateTime.Now });

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
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            _tray.Visible = true;
            _tray.ShowBalloonTip(1200, "Flair Messenger", "FM is still running in the system tray.", ToolTipIcon.Info);
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

            using var logoutWindow = new LogoutProgressForm();
            logoutWindow.Show(this);
            logoutWindow.Activate();
            Enabled = false;
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
                logoutWindow.Complete();
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

internal sealed class LogoutProgressForm : Form
{
    private bool _allowClose;

    public LogoutProgressForm()
    {
        Text = "Signing out";
        ClientSize = new Size(430, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Icon = LoginForm.AppIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24, 20, 24, 20),
            BackColor = Theme.Bg
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(layout);

        var title = new Label
        {
            Text = "Signing out of Second Life...",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(title, 0, 0);

        var explanation = new Label
        {
            Text = "Closing your avatar session safely. Please wait.",
            Dock = DockStyle.Fill,
            Font = Theme.Font,
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(explanation, 0, 1);

        var progress = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            Margin = Padding.Empty
        };
        layout.Controls.Add(progress, 0, 2);
    }

    public void Complete()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
