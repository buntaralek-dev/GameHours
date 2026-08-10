using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Newtonsoft.Json.Linq;
using Microsoft.Win32;

namespace SteamHoursChanger
{
    public partial class MainWindow : Window
    {
        private string _apiKey = "";
        private string _steamId = "";
        private string _selectedUserId = "";
        private string _steamPath = "";
        private Dictionary<string, UserInfo> _users = new Dictionary<string, UserInfo>();
        private Dictionary<int, GameInfo> _gamesData = new Dictionary<int, GameInfo>();
        private Dictionary<int, double> _originalHours = new Dictionary<int, double>();
        private Dictionary<int, double> _customHours = new Dictionary<int, double>();
        private Dictionary<int, GameInfo> _filteredData = new Dictionary<int, GameInfo>();
        private readonly HttpClient _httpClient = new HttpClient();
        private Border _selectedRow = null;
        private bool _autoLogin = false;

        private const string APP_DATA_DIR = "SteamHoursChanger";
        private string ConfigPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), APP_DATA_DIR, "steam_config.json");
        private string CustomHoursPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), APP_DATA_DIR, "custom_hours.json");

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            LoadCustomHours();
            ShowStep(0);
        }

        #region Config

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = JObject.Parse(File.ReadAllText(ConfigPath));
                    _apiKey = json["api_key"]?.ToString() ?? "";
                    _steamId = json["steam_id"]?.ToString() ?? "";
                    _steamPath = json["steam_path"]?.ToString() ?? "";
                    _autoLogin = json["auto_login"]?.Value<bool>() ?? false;

                    if (_autoLogin && !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_steamId) && !string.IsNullOrEmpty(_steamPath))
                    {
                        Dispatcher.BeginInvoke(new Action(async () => await AutoLoad()),
                            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        return;
                    }
                }
            }
            catch { }
        }

        private async Task AutoLoad()
        {
            var result = await GetSteamGames(_apiKey, _steamId);
            if (result.games == null)
            {
                ShowNotification("Ошибка загрузки. Проверьте данные.", false);
                ShowStep(1);
                return;
            }

            _gamesData = result.games;
            _originalHours = new Dictionary<int, double>();
            foreach (var kvp in result.games) _originalHours[kvp.Key] = kvp.Value.Hours;

            _users = GetSteamUsers();
            var autoDetected = DetectActiveAccountId();
            if (autoDetected != null)
            {
                _selectedUserId = autoDetected;
                ProtectLocalConfig(_selectedUserId);
                ShowStep(5);
            }
            else if (_users.Count == 1)
            {
                _selectedUserId = _users.Keys.First();
                ProtectLocalConfig(_selectedUserId);
                ShowStep(5);
            }
            else if (_users.Count > 1)
            {
                ShowStep(4);
            }
            else
            {
                ShowNotification("Аккаунты не найдены", false);
                ShowStep(1);
            }
        }

        private void SaveConfig(bool? autoLogin = null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath));
                var json = new JObject
                {
                    ["api_key"] = _apiKey,
                    ["steam_id"] = _steamId,
                    ["steam_path"] = _steamPath,
                    ["auto_login"] = autoLogin ?? _autoLogin
                };
                File.WriteAllText(ConfigPath, json.ToString());
                if (autoLogin.HasValue) _autoLogin = autoLogin.Value;
            }
            catch { }
        }

        private void LoadCustomHours()
        {
            try
            {
                if (File.Exists(CustomHoursPath))
                {
                    var json = JObject.Parse(File.ReadAllText(CustomHoursPath));
                    foreach (var prop in json.Properties())
                        if (int.TryParse(prop.Name, out int appid))
                            _customHours[appid] = prop.Value.Value<double>();
                }
            }
            catch { }
        }

        private void SaveCustomHours()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CustomHoursPath));
                var json = new JObject();
                foreach (var kvp in _customHours)
                    json[kvp.Key.ToString()] = kvp.Value;
                File.WriteAllText(CustomHoursPath, json.ToString());
            }
            catch { }
        }

        #endregion

        #region Steam Helpers

        private string GetLocalConfigPath(string userId) =>
            System.IO.Path.Combine(_steamPath, "userdata", userId, "config", "localconfig.vdf");

        private void SetFileReadOnly(string path, bool flag)
        {
            if (!File.Exists(path)) return;
            try { new FileInfo(path).IsReadOnly = flag; } catch { }
        }

        private void ProtectLocalConfig(string userId)
        {
            var p = GetLocalConfigPath(userId);
            if (File.Exists(p)) SetFileReadOnly(p, true);
        }

        private void UnprotectLocalConfig(string userId)
        {
            var p = GetLocalConfigPath(userId);
            if (File.Exists(p)) SetFileReadOnly(p, false);
        }

        private string FindSteamPath()
        {
            string[] paths = {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"D:\Steam",
                @"D:\Program Files\Steam",
                @"D:\Program Files (x86)\Steam",
                @"E:\Steam",
                @"E:\Program Files\Steam",
                @"E:\Program Files (x86)\Steam",
            };
            foreach (var path in paths)
                if (File.Exists(System.IO.Path.Combine(path, "steam.exe")))
                    return path;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var path = key.GetValue("InstallPath")?.ToString();
                        if (!string.IsNullOrEmpty(path) && File.Exists(System.IO.Path.Combine(path, "steam.exe")))
                            return path;
                    }
                }
            }
            catch { }
            return null;
        }

        private Dictionary<string, UserInfo> GetSteamUsers()
        {
            var users = new Dictionary<string, UserInfo>();
            if (string.IsNullOrEmpty(_steamPath)) return users;
            var userdataPath = System.IO.Path.Combine(_steamPath, "userdata");
            if (!Directory.Exists(userdataPath)) return users;

            foreach (var dir in Directory.GetDirectories(userdataPath))
            {
                var userId = System.IO.Path.GetFileName(dir);
                if (long.TryParse(userId, out _))
                    users[userId] = new UserInfo { Id = userId, Name = $"User_{userId}", Path = dir };
            }

            var loginFile = System.IO.Path.Combine(_steamPath, "config", "loginusers.vdf");
            if (File.Exists(loginFile))
            {
                try
                {
                    var content = File.ReadAllText(loginFile);
                    var pattern = @"""(\d+)""\s*\{[^}]*""AccountName""\s*""([^""]+)""";
                    foreach (Match match in Regex.Matches(content, pattern))
                    {
                        var userId = match.Groups[1].Value;
                        if (users.ContainsKey(userId))
                            users[userId].Name = match.Groups[2].Value;
                    }
                }
                catch { }
            }

            foreach (var userId in new List<string>(users.Keys))
            {
                if (users[userId].Name == $"User_{userId}")
                {
                    var lcp = System.IO.Path.Combine(users[userId].Path, "config", "localconfig.vdf");
                    if (File.Exists(lcp))
                    {
                        try
                        {
                            var content = File.ReadAllText(lcp);
                            var match = Regex.Match(content, @"""PersonaName""\s*""([^""]+)""");
                            if (match.Success) users[userId].Name = match.Groups[1].Value;
                        }
                        catch { }
                    }
                }
            }
            return users;
        }

        private string DetectActiveAccountId()
        {
            if (string.IsNullOrEmpty(_steamPath)) return null;

            var loginFile = System.IO.Path.Combine(_steamPath, "config", "loginusers.vdf");
            if (File.Exists(loginFile))
            {
                try
                {
                    var content = File.ReadAllText(loginFile);
                    var blocks = Regex.Matches(content, @"""(\d+)""\s*\{([^}]*)\}", RegexOptions.Singleline);
                    foreach (Match block in blocks)
                    {
                        var userId = block.Groups[1].Value;
                        var blockContent = block.Groups[2].Value;
                        if (Regex.IsMatch(blockContent, @"""MostRecent""\s*""1"""))
                            if (_users.ContainsKey(userId)) return userId;
                    }
                }
                catch { }
            }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var activeUser = key.GetValue("ActiveUser")?.ToString();
                        if (!string.IsNullOrEmpty(activeUser) && _users.ContainsKey(activeUser))
                            return activeUser;
                    }
                }
            }
            catch { }

            if (_users.Count == 1) return _users.Keys.First();
            return null;
        }

        #endregion

        #region API

        private async Task<(bool success, string message)> TestApiKey(string apiKey)
        {
            string url = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/";
            var parameters = new Dictionary<string, string>
            {
                ["key"] = apiKey,
                ["steamids"] = "76561197960435530"
            };
            try
            {
                var response = await _httpClient.GetAsync(url + "?" + await new FormUrlEncodedContent(parameters).ReadAsStringAsync());
                if (response.IsSuccessStatusCode)
                {
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if (json["response"]?["players"] != null) return (true, "");
                    return (false, "Ключ верный, но аккаунт закрыт");
                }
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return (false, "Доступ запрещён");
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (false, "Неверный ключ");
                return (false, $"Ошибка HTTP: {(int)response.StatusCode}");
            }
            catch (HttpRequestException) { return (false, "Нет интернета"); }
            catch { return (false, "Ошибка подключения"); }
        }

        private async Task<(Dictionary<int, GameInfo> games, string error)> GetSteamGames(string apiKey, string steamId)
        {
            string url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/";
            var parameters = new Dictionary<string, string>
            {
                ["key"] = apiKey,
                ["steamid"] = steamId,
                ["format"] = "json",
                ["include_appinfo"] = "true"
            };
            try
            {
                var response = await _httpClient.GetStringAsync(url + "?" + await new FormUrlEncodedContent(parameters).ReadAsStringAsync());
                var json = JObject.Parse(response);
                if (json["response"] == null) return (null, "Некорректный ответ API");
                if (json["response"]["games"] == null) return (null, "Аккаунт скрыт или нет игр");

                var games = new Dictionary<int, GameInfo>();
                foreach (var game in json["response"]["games"])
                {
                    int appid = game.Value<int>("appid");
                    string name = game.Value<string>("name");
                    if (appid <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                    double hours = game.Value<int>("playtime_forever") / 60.0;
                    games[appid] = new GameInfo { AppId = appid, Name = name, Hours = hours };
                }
                return (games, null);
            }
            catch (HttpRequestException ex) { return (null, $"Ошибка сети: {ex.Message}"); }
            catch (Exception ex) { return (null, $"Ошибка: {ex.Message}"); }
        }

        #endregion

        #region Edit Config

        private (bool success, string message) EditLocalConfig(string userId, int appid, double newHours)
        {
            if (string.IsNullOrEmpty(_steamPath))
                return (false, "Путь к Steam не указан");
            if (!Directory.Exists(_steamPath))
                return (false, $"Папка Steam не найдена:\n{_steamPath}");

            var path = GetLocalConfigPath(userId);
            if (!File.Exists(path))
                return (false, $"Файл не найден:\n{path}\n\nЗапустите игру хотя бы раз в Steam.");

            var backupPath = path + ".backup";
            if (!File.Exists(backupPath)) File.Copy(path, backupPath);

            try
            {
                var content = File.ReadAllText(path, Encoding.UTF8);
                var appidStr = appid.ToString();
                var pattern = @"""" + appidStr + @"""\s*\{([^}]*)\}";
                var match = Regex.Match(content, pattern, RegexOptions.Singleline);
                if (!match.Success) return (false, $"Игра {appid} не найдена в конфиге.\nЗапустите её хотя бы раз в Steam.");

                var block = match.Value;
                var newMinutes = (int)(newHours * 60);

                if (block.Contains("\"Playtime\""))
                    block = Regex.Replace(block, @"""Playtime""\s*""\d+""", $"\"Playtime\" \"{newMinutes}\"");
                else
                    block = block.Replace("}", $"\n\t\t\"Playtime\" \"{newMinutes}\"\n\t}}");

                if (block.Contains("\"Playtime2wks\""))
                    block = Regex.Replace(block, @"""Playtime2wks""\s*""\d+""", $"\"Playtime2wks\" \"{newMinutes}\"");

                File.WriteAllText(path, content.Replace(match.Value, block), Encoding.UTF8);
                return (true, "Часы изменены");
            }
            catch (UnauthorizedAccessException) { return (false, "Нет доступа к файлу. Закройте Steam и попробуйте снова."); }
            catch (Exception ex) { return (false, $"Ошибка: {ex.Message}"); }
        }

        #endregion

        #region Internet

        private bool DisableInternet()
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = "netsh", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false };
                psi.Arguments = "interface set interface \"Ethernet\" admin=DISABLED"; Process.Start(psi);
                psi.Arguments = "interface set interface \"Wi-Fi\" admin=DISABLED"; Process.Start(psi);
                psi.Arguments = "interface set interface \"Беспроводная сеть\" admin=DISABLED"; Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        private bool EnableInternet()
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = "netsh", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false };
                psi.Arguments = "interface set interface \"Ethernet\" admin=ENABLED"; Process.Start(psi);
                psi.Arguments = "interface set interface \"Wi-Fi\" admin=ENABLED"; Process.Start(psi);
                psi.Arguments = "interface set interface \"Беспроводная сеть\" admin=ENABLED"; Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        #endregion

        #region Notification

        private void ShowNotification(string message, bool success)
        {
            NotificationText.Text = message;
            NotificationBorder.Background = success
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x1A, 0x1A));
            NotificationBorder.BorderBrush = success
                ? new SolidColorBrush(Color.FromRgb(0x2A, 0x5A, 0x3A))
                : new SolidColorBrush(Color.FromRgb(0x5A, 0x2A, 0x2A));
            NotificationText.Foreground = success
                ? new SolidColorBrush(Color.FromRgb(0x6F, 0xFF, 0x9F))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

            NotificationBorder.Visibility = Visibility.Visible;
            NotificationBorder.Opacity = 0;
            NotificationBorder.RenderTransform = new TranslateTransform(0, -10);

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var slideIn = new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

            NotificationBorder.BeginAnimation(OpacityProperty, fadeIn);
            NotificationBorder.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                fadeOut.Completed += (s2, e2) => NotificationBorder.Visibility = Visibility.Collapsed;
                NotificationBorder.BeginAnimation(OpacityProperty, fadeOut);
            };
            timer.Start();
        }

        #endregion

        #region Button Spinner

        private object _originalBtnContent = null;

        private void ShowButtonSpinner(Button btn, bool show)
        {
            if (show)
            {
                _originalBtnContent = btn.Content;
                btn.IsEnabled = false;
                btn.Content = CreateSpinner();
            }
            else
            {
                btn.IsEnabled = true;
                if (_originalBtnContent != null)
                    btn.Content = _originalBtnContent;
            }
        }

        private UIElement CreateSpinner()
        {
            var grid = new Grid { Width = 18, Height = 18 };

            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x3E, 0x52)),
                StrokeThickness = 2,
                Opacity = 0.3
            };

            var arcPath = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x3E, 0x52)),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            arcPath.Data = Geometry.Parse("M9,1 A8,8 0 0,1 17,9");
            arcPath.RenderTransform = new RotateTransform();

            grid.Children.Add(ellipse);
            grid.Children.Add(arcPath);

            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var rotation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800));
            Storyboard.SetTarget(rotation, arcPath);
            Storyboard.SetTargetProperty(rotation, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            sb.Children.Add(rotation);

            grid.Loaded += (s, e) => sb.Begin();

            return grid;
        }

        #endregion

        #region Navigation

        private void ShowStep(int step)
        {
            var panels = new UIElement[] { ProgressPanel, ApiKeyPanel, SteamIdPanel, SteamPathPanel, AccountPanel, GamesPanel, WaitingPanel };
            UIElement target = step < panels.Length ? panels[step] : null;
            if (target == null) return;

            foreach (var p in panels)
            {
                if (p == target) continue;
                if (p.Visibility == Visibility.Visible)
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                    var captured = p;
                    fadeOut.Completed += (s, e) =>
                    {
                        captured.Visibility = Visibility.Collapsed;
                        AnimateIn(target);
                    };
                    captured.BeginAnimation(OpacityProperty, fadeOut);
                    goto PostAnimation;
                }
            }
            AnimateIn(target);

            PostAnimation:
            if (step == 0)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) => { timer.Stop(); ShowStep(1); };
                timer.Start();
            }
            else if (step == 1)
            {
                Dispatcher.BeginInvoke(new Action(() => ApiKeyBox.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (step == 2)
            {
                Dispatcher.BeginInvoke(new Action(() => SteamIdBox.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (step == 3)
            {
                var found = FindSteamPath();
                if (!string.IsNullOrEmpty(found))
                    _steamPath = found;
                SteamPathBox.Text = _steamPath ?? "";
            }
            else if (step == 4)
            {
                Dispatcher.BeginInvoke(new Action(LoadAccountList), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (step == 5)
            {
                Dispatcher.BeginInvoke(new Action(LoadGamesList), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void AnimateIn(UIElement panel)
        {
            panel.Visibility = Visibility.Visible;
            panel.Opacity = 0;
            panel.RenderTransform = new TranslateTransform(0, 12);

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var slideUp = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(250))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

            panel.BeginAnimation(OpacityProperty, fadeIn);
            panel.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }

        #endregion

        #region Event Handlers

        private async void OnApiKeyContinue(object sender, RoutedEventArgs e)
        {
            var key = ApiKeyBox.Text.Trim();
            if (string.IsNullOrEmpty(key)) { ShowNotification("Введите API ключ", false); return; }

            ShowButtonSpinner(ApiKeyBtn, true);
            await Task.Delay(2000);

            _apiKey = key;
            var result = await TestApiKey(key);
            ShowButtonSpinner(ApiKeyBtn, false);

            if (!result.success) { ShowNotification(result.message, false); return; }

            bool auto = ApiKeyCheckbox.IsChecked == true;
            SaveConfig(auto);
            ShowStep(2);
        }

        private void ApiKeyBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnApiKeyContinue(sender, e); }
        private void ApiKeyLink_Click(object sender, MouseButtonEventArgs e) =>
            Process.Start(new ProcessStartInfo { FileName = "https://steamcommunity.com/dev/apikey", UseShellExecute = true });

        private async void OnSteamIdContinue(object sender, RoutedEventArgs e)
        {
            var id = SteamIdBox.Text.Trim();
            if (string.IsNullOrEmpty(id)) { ShowNotification("Введите Steam ID", false); return; }
            if (!long.TryParse(id, out _) || id.Length < 10) { ShowNotification("Steam ID: только цифры, минимум 10", false); return; }

            ShowButtonSpinner(SteamIdBtn, true);
            await Task.Delay(2000);

            _steamId = id;
            var result = await GetSteamGames(_apiKey, id);
            ShowButtonSpinner(SteamIdBtn, false);

            if (result.games == null) { ShowNotification(result.error, false); return; }

            _gamesData = result.games;
            _originalHours = new Dictionary<int, double>();
            foreach (var kvp in result.games) _originalHours[kvp.Key] = kvp.Value.Hours;

            bool auto = SteamIdCheckbox.IsChecked == true;
            SaveConfig(auto);

            ShowStep(3);
        }

        private void SteamIdBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnSteamIdContinue(sender, e); }
        private void SteamIdLink_Click(object sender, MouseButtonEventArgs e) =>
            Process.Start(new ProcessStartInfo { FileName = "https://steamid.io", UseShellExecute = true });

        private async void OnSteamPathContinue(object sender, RoutedEventArgs e)
        {
            var path = SteamPathBox.Text.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(System.IO.Path.Combine(path, "steam.exe")))
            {
                ShowNotification("Путь неверный. steam.exe не найден в указанной папке.", false);
                return;
            }

            ShowButtonSpinner(SteamPathBtn, true);
            await Task.Delay(1000);

            _steamPath = path;
            bool auto = SteamPathCheckbox.IsChecked == true;
            SaveConfig(auto);

            ShowButtonSpinner(SteamPathBtn, false);

            _users = GetSteamUsers();
            var autoDetected = DetectActiveAccountId();

            if (autoDetected != null)
            {
                _selectedUserId = autoDetected;
                ProtectLocalConfig(_selectedUserId);
                ShowStep(5);
            }
            else if (_users.Count > 1)
            {
                ShowStep(4);
            }
            else if (_users.Count == 1)
            {
                _selectedUserId = _users.Keys.First();
                ProtectLocalConfig(_selectedUserId);
                ShowStep(5);
            }
            else
            {
                ShowNotification("Аккаунты Steam не найдены в указанной папке", false);
            }
        }

        private void SteamPathBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Выберите папку Steam" };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                SteamPathBox.Text = dialog.SelectedPath;
        }

        private void LoadAccountList()
        {
            AccountCombo.Items.Clear();
            if (_users.Count == 0) { AccountCombo.Items.Add("Аккаунты не найдены"); AccountContinueBtn.IsEnabled = false; return; }
            foreach (var user in _users.Values) AccountCombo.Items.Add(user.Name);
            if (AccountCombo.Items.Count > 0) AccountCombo.SelectedIndex = 0;
            AccountContinueBtn.IsEnabled = true;
        }

        private void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AccountCombo.SelectedItem != null)
            {
                var selectedName = AccountCombo.SelectedItem.ToString();
                foreach (var user in _users.Values)
                    if (user.Name == selectedName) { _selectedUserId = user.Id; break; }
            }
        }

        private void OnAccountContinue(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedUserId)) { ShowNotification("Выберите аккаунт", false); return; }
            ProtectLocalConfig(_selectedUserId);
            ShowStep(5);
        }

        #endregion

        #region Games List

        private void LoadGamesList()
        {
            _filteredData = new Dictionary<int, GameInfo>(_gamesData);
            UpdateGamesTable();
        }

        private void UpdateGamesTable()
        {
            GamesList.Children.Clear();
            _selectedRow = null;
            var dataToShow = _filteredData.Count > 0 ? _filteredData : _gamesData;
            var sorted = new List<KeyValuePair<int, GameInfo>>(dataToShow);
            sorted.Sort((a, b) => string.Compare(a.Value.Name, b.Value.Name));

            int index = 1;
            foreach (var kvp in sorted)
            {
                GamesList.Children.Add(CreateGameRow(kvp.Value, index, kvp.Key));
                index++;
            }
            GamesCountLabel.Text = $"{dataToShow.Count} игр";
        }

        private Border CreateGameRow(GameInfo game, int index, int appid)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var indexBlock = new TextBlock
            {
                Text = index.ToString(), Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize = 11, FontFamily = new FontFamily("Segoe UI Variable"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(indexBlock, 0);

            var nameBlock = new TextBlock
            {
                Text = game.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 12, FontFamily = new FontFamily("Segoe UI Variable"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameBlock, 1);

            double displayHours = _customHours.ContainsKey(appid) ? _customHours[appid] : game.Hours;
            string hoursText = displayHours >= 1000 ? (displayHours / 1000.0).ToString("F1") + "k" : displayHours.ToString("F1");

            var hoursBlock = new TextBlock
            {
                Text = hoursText,
                Foreground = _customHours.ContainsKey(appid) ?
                    new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)) :
                    new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontSize = 12, FontFamily = new FontFamily("Segoe UI Variable"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hoursBlock, 2);

            var appidBlock = new TextBlock
            {
                Text = appid.ToString(), Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize = 10, FontFamily = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(appidBlock, 3);

            grid.Children.Add(indexBlock);
            grid.Children.Add(nameBlock);
            grid.Children.Add(hoursBlock);
            grid.Children.Add(appidBlock);

            string bgColor = (index % 2 == 0) ? "#242424" : "#1E1E1E";
            var bgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor));

            var border = new Border
            {
                Background = bgBrush,
                Padding = new Thickness(12, 8, 12, 8),
                Cursor = Cursors.Hand,
                Child = grid,
                Tag = appid
            };

            border.MouseEnter += (s, e) =>
            {
                if (border == _selectedRow) return;
                var anim = new ColorAnimation(Color.FromRgb(0x30, 0x30, 0x30), TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            };

            border.MouseLeave += (s, e) =>
            {
                if (border == _selectedRow) return;
                var idx = GamesList.Children.IndexOf(border);
                var normalColor = (Color)ColorConverter.ConvertFromString((idx % 2 == 0) ? "#242424" : "#1E1E1E");
                var anim = new ColorAnimation(normalColor, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            };

            border.MouseLeftButtonDown += (s, e) => SelectGameRow(border);
            return border;
        }

        private void SelectGameRow(Border row)
        {
            if (_selectedRow != null)
            {
                var prevBrush = (SolidColorBrush)_selectedRow.Background;
                var prevIndex = GamesList.Children.IndexOf(_selectedRow);
                var prevColor = (Color)ColorConverter.ConvertFromString((prevIndex % 2 == 0) ? "#242424" : "#1E1E1E");
                var anim = new ColorAnimation(prevColor, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                prevBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            }

            _selectedRow = row;
            var selectedBrush = (SolidColorBrush)row.Background;
            var selectAnim = new ColorAnimation(Color.FromRgb(0x1A, 0x3A, 0x5C), TimeSpan.FromMilliseconds(200))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            selectedBrush.BeginAnimation(SolidColorBrush.ColorProperty, selectAnim);

            if (row.Tag is int appid && _gamesData.ContainsKey(appid))
            {
                double current = _customHours.ContainsKey(appid) ? _customHours[appid] : _gamesData[appid].Hours;
                HoursInput.Text = current.ToString("F1");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = SearchBox.Text.Trim().ToLower();
            _filteredData = new Dictionary<int, GameInfo>();
            if (string.IsNullOrEmpty(searchText))
                _filteredData = new Dictionary<int, GameInfo>(_gamesData);
            else
                foreach (var kvp in _gamesData)
                    if (kvp.Value.Name.ToLower().Contains(searchText))
                        _filteredData[kvp.Key] = kvp.Value;
            UpdateGamesTable();
        }

        #endregion

        #region Actions

        private void OnApplyHours(object sender, RoutedEventArgs e)
        {
            if (_selectedRow == null || !(_selectedRow.Tag is int appid))
            { ShowNotification("Выберите игру", false); return; }

            if (string.IsNullOrEmpty(_steamPath))
            { ShowNotification("Путь к Steam не задан", false); return; }

            if (string.IsNullOrEmpty(_selectedUserId))
            { ShowNotification("Аккаунт не выбран", false); return; }

            if (!double.TryParse(HoursInput.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double newHours) || newHours < 0)
            { ShowNotification("Введите корректное число", false); return; }

            UnprotectLocalConfig(_selectedUserId);
            var result = EditLocalConfig(_selectedUserId, (int)_selectedRow.Tag, newHours);
            ProtectLocalConfig(_selectedUserId);

            if (result.success)
            {
                _customHours[(int)_selectedRow.Tag] = newHours;
                SaveCustomHours();
                UpdateGamesTable();
                ShowNotification(result.message, true);
            }
            else ShowNotification(result.message, false);
        }

        private void OnResetHours(object sender, RoutedEventArgs e)
        {
            if (_selectedRow == null || !(_selectedRow.Tag is int appid))
            { ShowNotification("Выберите игру", false); return; }

            int appid = (int)_selectedRow.Tag;

            if (!_customHours.ContainsKey(appid))
            { ShowNotification("Нет изменённых часов", false); return; }

            UnprotectLocalConfig(_selectedUserId);
            double original = _originalHours.ContainsKey(appid) ? _originalHours[appid] : 0;
            var result = EditLocalConfig(_selectedUserId, appid, original);
            ProtectLocalConfig(_selectedUserId);

            if (result.success)
            {
                _customHours.Remove(appid);
                SaveCustomHours();
                UpdateGamesTable();
                ShowNotification("Часы сброшены", true);
            }
            else ShowNotification(result.message, false);
        }

        private void OnRefreshGames(object sender, RoutedEventArgs e) => LoadGamesList();

        private void OnRestartSteam(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/f /im steam.exe", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
                Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/f /im steamwebhelper.exe", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
                System.Threading.Thread.Sleep(2000);
            }
            catch { }

            if (!DisableInternet())
                ShowNotification("Не удалось отключить интернет. Отключите вручную.", false);

            ShowStep(6);
        }

        private void OnEnableInternet(object sender, RoutedEventArgs e)
        {
            if (!EnableInternet())
            { ShowNotification("Не удалось включить интернет", false); return; }
            ShowNotification("Интернет включён", true);
            ShowStep(5);
        }

        private void OnLaunchSteam(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_steamPath))
            {
                var steamExe = System.IO.Path.Combine(_steamPath, "steam.exe");
                if (File.Exists(steamExe)) Process.Start(steamExe);
                else ShowNotification($"Steam не найден: {steamExe}", false);
            }
        }

        #endregion

        #region Window

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            this.DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_selectedUserId))
                UnprotectLocalConfig(_selectedUserId);
            SaveCustomHours();
            this.Close();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;

        #endregion
    }

    public class UserInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }

    public class GameInfo
    {
        public int AppId { get; set; }
        public string Name { get; set; }
        public double Hours { get; set; }
    }
}
