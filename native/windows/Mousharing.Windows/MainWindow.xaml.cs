using System.Collections.ObjectModel;
using System.Net;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mousharing.Windows.Interop;
using Mousharing.Windows.Models;
using Mousharing.Windows.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace Mousharing.Windows;

public sealed partial class MainWindow : Window
{
    private readonly Settings settings;
    private readonly SessionController session;
    private readonly DiscoveryService discovery;
    private readonly TrayIcon tray;
    private readonly nint handle;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer discoveryExpiry;
    private readonly ObservableCollection<DiscoveredMaster> masters = [];
    private bool initialized, exiting;
    public MainWindow(bool smokeTest = false)
    {
        InitializeComponent();
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new SizeInt32(820, 940));
        settings = smokeTest ? new Settings() : Settings.Load();
        session = new(settings, DispatcherQueue, handle); session.Changed += Refresh;
        discovery = new(); session.Advertise = discovery.Advertise;
        discovery.Found += value => DispatcherQueue.TryEnqueue(() =>
        {
            int existing = -1; for (int i = 0; i < masters.Count; i++) if (masters[i].Id == value.Id) existing = i;
            if (existing >= 0) masters[existing] = value; else if (masters.Count < 32 && value.Id != settings.DeviceId) masters.Add(value);
        });
        discovery.Status += text => DispatcherQueue.TryEnqueue(() => ShowMessage(text));
        discoveryExpiry = DispatcherQueue.CreateTimer(); discoveryExpiry.Interval = TimeSpan.FromSeconds(3);
        discoveryExpiry.Tick += (_, _) => { for (int i = masters.Count - 1; i >= 0; i--) if (DateTime.UtcNow - masters[i].LastSeen > TimeSpan.FromSeconds(10)) masters.RemoveAt(i); };
        if (!smokeTest) discoveryExpiry.Start();
        MasterList.ItemsSource = masters;
        DeviceNameBox.Text = settings.DeviceName; RoleBox.SelectedIndex = settings.IsMaster ? 0 : 1;
        SideBox.SelectedIndex = settings.Side switch { "left" => 1, "top" => 2, "bottom" => 3, _ => 0 };
        ClipboardToggle.IsOn = settings.ClipboardEnabled; EdgeToggle.IsOn = settings.EdgeSwitchEnabled; AddressBox.Text = settings.ManualAddress;
        tray = new() { Open = OpenWindow, Pause = () => session.TogglePause(), Exit = Exit, IsPaused = () => session.Paused };
        AppWindow.Closing += (_, args) => { if (!exiting && tray.Available) { args.Cancel = true; NativeMethods.ShowWindow(handle, 0); } else if (!exiting) { exiting = true; session.Dispose(); discovery.Dispose(); tray.Dispose(); } };
        Closed += (_, _) => { discoveryExpiry.Stop(); session.Dispose(); discovery.Dispose(); tray.Dispose(); };
        initialized = true; if (!smokeTest) discovery.Start(); Refresh();
    }
    public void VerifySmokeAndClose()
    {
        if (Content.XamlRoot is null || !DeviceNameBox.IsLoaded || !ConnectButton.IsLoaded || !StatusBar.IsLoaded || session.Active || session.Connected)
            throw new InvalidOperationException("UI smoke verification failed");
        Exit();
    }
    private void OpenWindow() { NativeMethods.ShowWindow(handle, 9); Activate(); }
    private void Exit() { exiting = true; session.Dispose(); discovery.Dispose(); tray.Dispose(); Close(); }
    private void ShowMessage(string text, InfoBarSeverity severity = InfoBarSeverity.Informational) { StatusBar.Title = text; StatusBar.Message = ""; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
    private void Refresh()
    {
        if (!initialized) return;
        bool active = session.Active;
        RoleBox.IsEnabled = DeviceNameBox.IsEnabled = SideBox.IsEnabled = ClipboardToggle.IsEnabled = EdgeToggle.IsEnabled = !active && !session.Paused;
        InvitationBox.IsEnabled = MasterList.IsEnabled = AddressBox.IsEnabled = !active && !session.Paused;
        MasterPanel.Visibility = RoleBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClientPanel.Visibility = RoleBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        RoleHelp.Text = RoleBox.SelectedIndex == 0 ? "Mouse dan keyboard yang terpasang di Windows ini akan mengontrol perangkat pasangan." : "Mouse dan keyboard perangkat master akan mengontrol Windows ini.";
        RememberedPeerLabel.Text = settings.MasterName is null ? "Belum ada master yang tersimpan. Tempel undangan sekali untuk pairing." : "Master tersimpan: " + settings.MasterName + ". Undangan tidak perlu ditempel ulang.";
        ConnectButton.Content = RoleBox.SelectedIndex == 0 ? "Mulai berbagi" : "Hubungkan";
        ConnectButton.IsEnabled = !active && !session.Paused;
        DisconnectButton.IsEnabled = active || session.Paused;
        PauseButton.IsEnabled = active || session.Paused;
        PauseButton.Content = session.Paused ? "Lanjutkan" : "Jeda";
        BusyRing.IsActive = active && !session.Connected;
        ControlLabel.Text = session.Control;
        ShowMessage(session.Status, session.Connected ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        tray.Update(session.Status);
        DrawScreens();
        if (Settings.LoadFailure is not null) { ConnectButton.IsEnabled = PauseButton.IsEnabled = CopyInvitationButton.IsEnabled = ForgetPairingButton.IsEnabled = false; ShowMessage(Settings.LoadFailure, InfoBarSeverity.Error); }
    }
    private void ApplySettings()
    {
        string name = DeviceNameBox.Text.Trim();
        if (name.Length is < 1 or > 128) throw new InvalidOperationException("Isi nama perangkat, maksimum 128 karakter.");
        string address = AddressBox.Text.Trim();
        if (address.Length > 0 && (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) throw new InvalidOperationException("Alamat manual harus berupa IPv4, contohnya 192.168.1.10.");
        settings.DeviceName = name; settings.IsMaster = RoleBox.SelectedIndex == 0;
        settings.Side = ((ComboBoxItem)SideBox.SelectedItem).Tag.ToString() ?? "right";
        settings.ClipboardEnabled = ClipboardToggle.IsOn; settings.EdgeSwitchEnabled = EdgeToggle.IsOn; settings.ManualAddress = address;
        settings.Save();
    }
    private void ConnectClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplySettings();
            if (!settings.IsMaster && !string.IsNullOrWhiteSpace(InvitationBox.Password)) { session.ImportInvitation(InvitationBox.Password); InvitationBox.Password = ""; }
            if (!settings.IsMaster && MasterList.SelectedItem is DiscoveredMaster master)
            {
                if (master.Id != settings.MasterId) throw new InvalidOperationException("Perangkat yang dipilih berbeda dari undangan. Tempel undangan perangkat tersebut terlebih dahulu.");
                settings.ManualAddress = master.Address; settings.Save();
            }
            session.Start(); Refresh();
        }
        catch (Exception exception) { ShowMessage(exception.Message, InfoBarSeverity.Error); }
    }
    private void DisconnectClicked(object sender, RoutedEventArgs e) { session.Stop(); Refresh(); }
    private void PauseClicked(object sender, RoutedEventArgs e) { session.TogglePause(); Refresh(); }
    private void RoleChanged(object sender, SelectionChangedEventArgs e) { if (initialized) Refresh(); }
    private void SideChanged(object sender, SelectionChangedEventArgs e) { if (initialized) DrawScreens(); }
    private void MasterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !session.Active && MasterList.SelectedItem is DiscoveredMaster value) AddressBox.Text = value.Address;
    }
    private void CopyInvitation(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!session.Active) ApplySettings();
            var package = new DataPackage(); package.SetText(session.MakeInvitation()); Clipboard.SetContent(package); Clipboard.Flush();
            ShowMessage("Undangan disalin. Tempel di aplikasi Mousharing pada perangkat client.", InfoBarSeverity.Success);
        }
        catch (Exception exception) { ShowMessage("Undangan belum bisa disalin: " + exception.Message, InfoBarSeverity.Error); }
    }
    private async void ForgetPairing(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Hapus pairing?", Content = "Koneksi akan dihentikan dan akses pasangan lama dicabut. Buat atau tempel undangan baru untuk menyambungkan kembali.", PrimaryButtonText = "Hapus pairing", CloseButtonText = "Batal", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { try { session.ForgetPairing(); InvitationBox.Password = ""; Refresh(); } catch (Exception exception) { ShowMessage(exception.Message, InfoBarSeverity.Error); } }
    }
    private void DrawScreens()
    {
        ScreenPreview.Children.Clear(); ScreenPreview.RowDefinitions.Clear(); ScreenPreview.ColumnDefinitions.Clear();
        string side = (SideBox.SelectedItem as ComboBoxItem)?.Tag.ToString() ?? "right";
        bool vertical = side is "top" or "bottom";
        if (vertical) { ScreenPreview.RowDefinitions.Add(new() { Height = GridLength.Auto }); ScreenPreview.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        else { ScreenPreview.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ScreenPreview.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); }
        string localName = DeviceNameBox.Text.Trim().Length > 0 ? DeviceNameBox.Text.Trim() : "Windows ini";
        string master = RoleBox.SelectedIndex == 0 ? localName : settings.MasterName ?? "Master";
        string client = RoleBox.SelectedIndex == 0 ? "Client" : localName;
        for (int i = 0; i < 2; i++)
        {
            bool isMaster = side is "left" or "top" ? i == 1 : i == 0;
            var card = new Border { Width = 190, MinHeight = 100, Margin = new(6), Padding = new(14), CornerRadius = new(7), BorderThickness = new(2), BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
            var text = new StackPanel { Spacing = 6 }; text.Children.Add(new TextBlock { Text = isMaster ? master : client, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 }); text.Children.Add(new TextBlock { Text = isMaster ? "Master · sumber input" : "Client · menerima input", TextWrapping = TextWrapping.Wrap, FontSize = 12 }); card.Child = text;
            if (vertical) Grid.SetRow(card, i); else Grid.SetColumn(card, i); ScreenPreview.Children.Add(card);
        }
    }
}
