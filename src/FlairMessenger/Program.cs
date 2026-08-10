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
    public static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    public static readonly string MessagesPath = Path.Combine(DataDir, "messages.json");
    public static readonly string IconPath = Path.Combine(Root, "assets", "fmicon.png");

    public static T Read<T>(string path, T fallback)
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

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string value)
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

    public event Action<string>? Status;
    public event Action<ChatRecord>? MessageReceived;
    public event Action? FriendsChanged;
    public event Action? GroupsChanged;

    public IReadOnlyDictionary<LMUUID, FriendInfo> Friends => _client.Friends.FriendList;
    public IReadOnlyDictionary<LMUUID, Group> Groups => _groups;
    public bool IsLoggedIn => _client.Network.Connected;

    public SecondLifeService()
    {
        _client.Self.IM += (_, e) =>
        {
            dynamic im = e.IM;
            string text = Convert.ToString(im.Message) ?? "";
            if (string.IsNullOrWhiteSpace(text)) return;

            string fromName = Convert.ToString(im.FromAgentName) ?? "Second Life";
            LMUUID fromId = im.FromAgentID;
            bool isGroup = false;
            LMUUID groupOrSessionId = LMUUID.Zero;
            try { isGroup = Convert.ToBoolean(im.GroupIM); } catch { }
            try { groupOrSessionId = im.IMSessionID; } catch { }
            var conversationId = isGroup && groupOrSessionId != LMUUID.Zero
                ? $"group:{groupOrSessionId}"
                : fromId.ToString();
            var conversationName = isGroup
                ? (_groups.TryGetValue(groupOrSessionId, out var group) ? group.Name : "Group IM")
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
        _client.Friends.FriendOnline += (_, _) => FriendsChanged?.Invoke();
        _client.Friends.FriendOffline += (_, _) => FriendsChanged?.Invoke();
        _client.Friends.FriendNames += (_, _) => FriendsChanged?.Invoke();
        _client.Groups.CurrentGroups += (_, e) =>
        {
            _groups.Clear();
            foreach (var pair in e.Groups) _groups[pair.Key] = pair.Value;
            GroupsChanged?.Invoke();
        };
        _client.Network.Disconnected += (_, e) => Status?.Invoke($"Verbinding verbroken: {e.Reason}");
    }

    public async Task<(bool Success, string Message)> LoginAsync(string loginName, string password, string start, CancellationToken token)
    {
        var parts = SplitLoginName(loginName);
        Status?.Invoke("Inloggen bij Second Life...");
        var loginParams = _client.Network.DefaultLoginParams(parts.First, parts.Last, password, "Flair Messenger", "0.2");
        loginParams.Start = start.Equals("home", StringComparison.OrdinalIgnoreCase) ? "home" : "last";
        loginParams.URI = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
        loginParams.UserAgent = "Flair Messenger 0.2";

        try
        {
            var response = await _client.Network.LoginWithResponseAsync(loginParams, token);
            if (response is null)
                return (false, "Geen loginantwoord ontvangen van Second Life.");
            if (!response.Success)
                return (false, string.IsNullOrWhiteSpace(response.Message) ? "Login mislukt." : response.Message);

            Status?.Invoke("Ingelogd. Offline IMs ophalen...");
            try { await _client.Self.RetrieveInstantMessagesAsync(token); } catch { }
            try { _client.Groups.RequestCurrentGroups(); } catch { }
            FriendsChanged?.Invoke();
            GroupsChanged?.Invoke();
            return (true, "Ingelogd bij Second Life.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public bool SendInstantMessage(string avatarId, string text)
    {
        if (!LMUUID.TryParse(avatarId, out var id) || string.IsNullOrWhiteSpace(text)) return false;
        _client.Self.InstantMessage(id, text.Trim());
        return true;
    }

    public bool SendGroupMessage(string groupId, string text)
    {
        if (!LMUUID.TryParse(groupId, out var id) || string.IsNullOrWhiteSpace(text)) return false;
        _client.Self.InstantMessageGroup(id, text.Trim());
        return true;
    }

    public void Logout()
    {
        if (_client.Network.Connected) _client.Network.Logout();
    }

    public void Dispose()
    {
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
    private readonly TextBox _login = Box();
    private readonly TextBox _password = Box();
    private readonly ComboBox _location = new();
    private readonly CheckBox _remember = new();
    private readonly Label _error = Label("");
    private readonly Button _loginButton = Button("Login");

    public LoginForm()
    {
        Text = "Flair Messenger Login";
        Size = new Size(440, 430);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Icon = AppIcon();

        var settings = Store.Read(Store.SettingsPath, new AppSettings());
        if (settings.Remember)
        {
            _login.Text = settings.LoginName;
            _password.Text = Store.Unprotect(settings.Password);
            _remember.Checked = true;
        }

        AddHeader(this);
        Add(Label("Loginnaam:", 32, 112), _login, 32, 138, 350);
        Add(Label("Wachtwoord:", 32, 180), _password, 32, 206, 350);
        _password.UseSystemPasswordChar = true;

        Controls.Add(Label("Locatie waar in te loggen:", 32, 248));
        _location.SetBounds(32, 274, 350, 30);
        _location.DropDownStyle = ComboBoxStyle.DropDownList;
        _location.BackColor = Theme.Input;
        _location.ForeColor = Theme.Text;
        _location.Items.AddRange(["Home", "Laatste locatie"]);
        _location.SelectedIndex = settings.Location == "home" ? 0 : 1;
        Controls.Add(_location);

        _remember.Text = "Gegevens opslaan";
        _remember.SetBounds(32, 316, 180, 28);
        _remember.ForeColor = Theme.Muted;
        _remember.BackColor = Theme.Bg;
        Controls.Add(_remember);

        _loginButton.SetBounds(246, 316, 136, 36);
        _loginButton.Click += LoginClicked;
        Controls.Add(_loginButton);

        _error.SetBounds(32, 360, 350, 26);
        _error.ForeColor = Color.FromArgb(248, 113, 113);
        Controls.Add(_error);
    }

    private async void LoginClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_login.Text) || string.IsNullOrWhiteSpace(_password.Text))
        {
            _error.Text = "Vul loginnaam en wachtwoord in.";
            return;
        }

        _loginButton.Enabled = false;
        _error.ForeColor = Theme.Muted;
        _error.Text = "Verbinden met Second Life...";

        var settings = new AppSettings
        {
            Remember = _remember.Checked,
            LoginName = _remember.Checked ? _login.Text.Trim() : "",
            Password = _remember.Checked ? Store.Protect(_password.Text) : "",
            Location = _location.SelectedIndex == 0 ? "home" : "last"
        };
        Store.Write(Store.SettingsPath, settings);

        var service = new SecondLifeService();
        var result = await service.LoginAsync(_login.Text, _password.Text, settings.Location, CancellationToken.None);
        if (!result.Success)
        {
            service.Dispose();
            _error.ForeColor = Color.FromArgb(248, 113, 113);
            _error.Text = result.Message;
            _loginButton.Enabled = true;
            return;
        }

        Hide();
        new MainForm(service, _login.Text.Trim(), settings.Location).Show();
    }

    private static void AddHeader(Control parent)
    {
        if (File.Exists(Store.IconPath))
        {
            var logo = new PictureBox { Image = Image.FromFile(Store.IconPath), SizeMode = PictureBoxSizeMode.Zoom };
            logo.SetBounds(26, 24, 64, 64);
            parent.Controls.Add(logo);
        }
        parent.Controls.Add(Label("Flair Messenger", 106, 30, 260, 30, 18, true));
        parent.Controls.Add(Label("FM - Second Life", 108, 62, 180, 24, 10));
    }

    internal static Icon AppIcon()
    {
        try { return File.Exists(Store.IconPath) ? Icon.FromHandle(new Bitmap(Store.IconPath).GetHicon()) : SystemIcons.Application; }
        catch { return SystemIcons.Application; }
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
    private readonly SecondLifeService _service;
    private readonly string _loginName;
    private readonly string _location;
    private readonly List<ChatRecord> _messages;
    private readonly List<ChatRecord> _notifications;
    private readonly ListBox _conversations = new();
    private readonly ListBox _friendsList = new();
    private readonly ListBox _groupsList = new();
    private readonly RichTextBox _messageFeed = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Panel _content = new();
    private readonly NotifyIcon _tray;
    private ConversationItem _active = new("system", "System", ConversationKind.System);

    public MainForm(SecondLifeService service, string loginName, string location)
    {
        _service = service;
        _loginName = loginName;
        _location = location;
        _messages = Store.Read(Store.MessagesPath, new List<ChatRecord>());
        _notifications = new List<ChatRecord>();

        Text = "Flair Messenger (FM)";
        Size = new Size(1100, 720);
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Icon = LoginForm.AppIcon();

        _tray = new NotifyIcon { Icon = Icon, Text = "Flair Messenger", Visible = false, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Items.Add("Open Flair Messenger", null, (_, _) => RestoreFromTray());
        _tray.ContextMenuStrip.Items.Add("Afsluiten", null, (_, _) => Close());
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        BuildShell();
        WireSecondLifeEvents();
        AddSystem("Ingelogd bij Second Life.");
        ShowChats();
        RefreshAll();
    }

    private void WireSecondLifeEvents()
    {
        _service.MessageReceived += record => BeginInvoke(() =>
        {
            AddMessage(record);
            AddNotification($"Nieuw bericht van {record.Sender}");
            RefreshAll();
            SelectConversation(record.ConversationId);
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
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;
        Controls.Add(_content);

        var rail = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = Theme.Rail, Padding = new Padding(12) };
        Controls.Add(rail);

        var brand = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Rail };
        rail.Controls.Add(brand);
        if (File.Exists(Store.IconPath))
        {
            var logo = new PictureBox { Image = Image.FromFile(Store.IconPath), SizeMode = PictureBoxSizeMode.Zoom };
            logo.SetBounds(4, 12, 52, 52);
            brand.Controls.Add(logo);
        }
        brand.Controls.Add(ShellLabel("Flair Messenger", 66, 15, 132, 24, 12, true));
        brand.Controls.Add(ShellLabel("Second Life IM", 66, 40, 132, 20, 9));

        foreach (var (text, action) in new (string, Action)[]
        {
            ("Chats", ShowChats),
            ("Friends", ShowFriends),
            ("Groups", ShowGroups),
            ("Notifications", ShowNotifications),
            ("Settings", ShowSettings),
            ("About", ShowAbout)
        }.Reverse())
        {
            var button = NavButton(text);
            button.Click += (_, _) => action();
            rail.Controls.Add(button);
            button.BringToFront();
        }
        brand.BringToFront();

        rail.BringToFront();
    }

    private void ShowChats()
    {
        _content.Controls.Clear();
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 1,
            BackColor = Theme.Bg,
            Panel1MinSize = 220,
            Panel2MinSize = 500
        };
        _content.Controls.Add(split);
        split.HandleCreated += (_, _) => SetConversationSplitter(split);
        split.SizeChanged += (_, _) => SetConversationSplitter(split);

        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(14) };
        split.Panel1.Controls.Add(left);
        left.Controls.Add(ShellLabel("Conversations", 0, 0, 210, 28, 12, true));

        _conversations.SetBounds(0, 40, 232, left.Height - 40);
        _conversations.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        StyleList(_conversations);
        _conversations.SelectedIndexChanged -= ConversationChanged;
        _conversations.SelectedIndexChanged += ConversationChanged;
        left.Controls.Add(_conversations);

        var chatLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Bg };
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
        _subtitle.Text = $"{_loginName} - {(_location == "home" ? "Home" : "Laatste locatie")}";
        _subtitle.Dock = DockStyle.Top;
        _subtitle.Height = 22;
        _subtitle.Font = new Font("Segoe UI", 9);
        _subtitle.ForeColor = Theme.Muted;
        _subtitle.BackColor = Color.Transparent;
        header.Controls.Add(_subtitle);
        header.Controls.Add(_title);
        chatLayout.Controls.Add(header, 0, 0);

        _messageFeed.Dock = DockStyle.Fill;
        _messageFeed.ReadOnly = true;
        _messageFeed.BorderStyle = BorderStyle.None;
        _messageFeed.BackColor = Theme.Bg;
        _messageFeed.ForeColor = Theme.Text;
        _messageFeed.Font = new Font("Segoe UI", 10);
        chatLayout.Controls.Add(_messageFeed, 0, 1);

        var composer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 14, 18, 14) };
        var input = LoginForm.Box();
        input.Multiline = false;
        input.Dock = DockStyle.Fill;
        var send = LoginForm.Button("Send");
        send.Dock = DockStyle.Right;
        send.Width = 96;
        send.Click += (_, _) => SendActive(input);
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendActive(input);
            }
        };
        composer.Controls.Add(input);
        composer.Controls.Add(send);
        chatLayout.Controls.Add(composer, 0, 2);

        RefreshConversations();
        RenderMessages();
    }

    private static void SetConversationSplitter(SplitContainer split)
    {
        var max = split.Width - split.Panel2MinSize - split.SplitterWidth;
        if (max <= split.Panel1MinSize) return;
        split.SplitterDistance = Math.Min(260, max);
    }

    private void ShowFriends()
    {
        ShowListPage("Friends", _friendsList, () =>
        {
            _friendsList.Items.Clear();
            foreach (var friend in _service.Friends.Values.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Name))
                _friendsList.Items.Add(new ConversationItem(friend.UUID.ToString(), $"{friend.Name} - {(friend.IsOnline ? "Online" : "Offline")}", ConversationKind.Private));
            if (_friendsList.Items.Count == 0) _friendsList.Items.Add("Geen friends geladen.");
        });
        _friendsList.DoubleClick -= OpenSelectedFriend;
        _friendsList.DoubleClick += OpenSelectedFriend;
    }

    private void ShowGroups()
    {
        ShowListPage("Groups", _groupsList, () =>
        {
            _groupsList.Items.Clear();
            foreach (var group in _service.Groups.Values.OrderBy(g => g.Name))
                _groupsList.Items.Add(new ConversationItem($"group:{group.ID}", $"{group.Name} - {group.GroupMembershipCount} leden", ConversationKind.Group));
            if (_groupsList.Items.Count == 0) _groupsList.Items.Add("Geen groepen geladen.");
        });
        _groupsList.DoubleClick -= OpenSelectedGroup;
        _groupsList.DoubleClick += OpenSelectedGroup;
    }

    private void ShowNotifications()
    {
        var list = new ListBox();
        ShowListPage("Notifications", list, () =>
        {
            foreach (var item in _notifications.OrderByDescending(n => n.Time))
                list.Items.Add($"[{item.Time:HH:mm}] {item.Text}");
            if (list.Items.Count == 0) list.Items.Add("Geen notificaties.");
        });
    }

    private void ShowSettings() => ShowTextPage("Settings", new[]
    {
        $"Loginnaam: {_loginName}",
        $"Loginlocatie: {(_location == "home" ? "Home" : "Laatste locatie")}",
        "Gegevens opslaan staat op het loginvenster.",
        "Minimaliseren verplaatst FM naar de system tray.",
        "Chats worden lokaal bewaard in de data-map naast de BAT."
    });

    private void ShowAbout() => ShowTextPage("About", new[]
    {
        "Flair Messenger (FM)",
        "Second Life login en IM via LibreMetaverse.",
        "Private IM, Friends, Groups, Notifications, Settings en tray-modus.",
        "Versie 0.3"
    });

    private void ShowListPage(string title, ListBox list, Action fill)
    {
        _content.Controls.Clear();
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(26) };
        _content.Controls.Add(panel);
        var label = ShellLabel(title, 0, 0, 500, 36, 18, true);
        panel.Controls.Add(label);
        list.SetBounds(0, 56, panel.Width - 52, panel.Height - 82);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        StyleList(list);
        panel.Controls.Add(list);
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
            _title.Text = item.Name;
            RenderMessages();
        }
    }

    private void OpenSelectedFriend(object? sender, EventArgs e)
    {
        if (_friendsList.SelectedItem is ConversationItem item)
        {
            _active = item with { Name = item.Name.Split(" - ")[0] };
            ShowChats();
            SelectConversation(_active.Id);
        }
    }

    private void OpenSelectedGroup(object? sender, EventArgs e)
    {
        if (_groupsList.SelectedItem is ConversationItem item)
        {
            _active = item with { Name = item.Name.Split(" - ")[0] };
            ShowChats();
            SelectConversation(_active.Id);
        }
    }

    private void SendActive(TextBox input)
    {
        var text = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        bool sent = _active.Kind switch
        {
            ConversationKind.Private => _service.SendInstantMessage(_active.Id, text),
            ConversationKind.Group => _service.SendGroupMessage(_active.Id.Replace("group:", ""), text),
            _ => false
        };
        if (!sent)
        {
            AddSystem("Kies eerst een friend of group om een bericht te sturen.");
            RefreshConversations();
            SelectConversation("system");
            return;
        }

        AddMessage(new ChatRecord { ConversationId = _active.Id, ConversationName = _active.Name, Sender = "Ik", Text = text, Time = DateTime.Now });
        input.Clear();
        RefreshConversations();
        SelectConversation(_active.Id);
        RenderMessages();
    }

    private void RefreshAll()
    {
        RefreshConversations();
        if (_friendsList.Parent is not null) ShowFriends();
        if (_groupsList.Parent is not null) ShowGroups();
    }

    private void RefreshConversations()
    {
        var selected = _active.Id;
        _conversations.Items.Clear();
        _conversations.Items.Add(new ConversationItem("system", "System", ConversationKind.System));

        foreach (var friend in _service.Friends.Values.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Name))
            _conversations.Items.Add(new ConversationItem(friend.UUID.ToString(), friend.Name, ConversationKind.Private));

        foreach (var group in _service.Groups.Values.OrderBy(g => g.Name))
            _conversations.Items.Add(new ConversationItem($"group:{group.ID}", group.Name, ConversationKind.Group));

        foreach (var msg in _messages.Where(m => m.ConversationId != "system").GroupBy(m => m.ConversationId))
        {
            if (_conversations.Items.Cast<ConversationItem>().All(i => i.Id != msg.Key))
            {
                var kind = msg.Key.StartsWith("group:", StringComparison.OrdinalIgnoreCase) ? ConversationKind.Group : ConversationKind.Private;
                _conversations.Items.Add(new ConversationItem(msg.Key, msg.Last().ConversationName, kind));
            }
        }

        SelectConversation(selected);
    }

    private void SelectConversation(string id)
    {
        for (var i = 0; i < _conversations.Items.Count; i++)
        {
            if (_conversations.Items[i] is ConversationItem item && item.Id == id)
            {
                _conversations.SelectedIndex = i;
                _active = item;
                _title.Text = item.Name;
                RenderMessages();
                return;
            }
        }
        if (_conversations.Items.Count > 0)
        {
            _conversations.SelectedIndex = 0;
            _active = (ConversationItem)_conversations.Items[0];
            _title.Text = _active.Name;
            RenderMessages();
        }
    }

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
        Store.Write(Store.MessagesPath, _messages);
    }

    private static Button NavButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 46,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Rail,
            ForeColor = Theme.Muted,
            Font = Theme.Font,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0)
        };
        button.FlatAppearance.BorderSize = 0;
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
        Activate();
        _tray.Visible = false;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            _tray.Visible = true;
            _tray.ShowBalloonTip(1200, "Flair Messenger", "FM draait verder in de tray.", ToolTipIcon.Info);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        _service.Dispose();
        base.OnFormClosing(e);
    }

    private sealed record ConversationItem(string Id, string Name, ConversationKind Kind)
    {
        public override string ToString() => Name;
    }

    private enum ConversationKind
    {
        System,
        Private,
        Group
    }
}
