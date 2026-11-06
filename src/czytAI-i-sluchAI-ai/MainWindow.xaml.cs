using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using czytAI_i_sluchAI_ai.Agent;

namespace czytAI_i_sluchAI_ai
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int HotKeyId = 9001;
        private const uint ModControl = 0x0002;
        private const uint Vk0 = 0x30;
        private const int WmHotKey = 0x0312;
        private const string PresetPrompt = "Read the lecture on my screen and determine its core subject and vibe. To give me maximum immersion while I consume the text, play a specific, perfectly fitting background song.";

        private HwndSource? _windowSource;
        private LocalAgent? _agent;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainWindow()
        {
            InitializeComponent();
            ConversationTextBox.Text = "Connects to an OpenAI-compatible local model endpoint.\r\n\r\n";

            // Disable inputs while the agent initializes
            PromptTextBox.IsEnabled = false;
            PresetPromptButton.IsEnabled = false;

            // Start the asynchronous initialization
            _ = InitializeAgentAsync();
        }

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            _windowSource = (HwndSource)PresentationSource.FromVisual(this)!;
            _windowSource.AddHook(WindowMessageHook);

            var handle = new WindowInteropHelper(this).Handle;
            if (!RegisterHotKey(handle, HotKeyId, ModControl, Vk0))
            {
                var error = Marshal.GetLastWin32Error();
                ConversationTextBox.AppendText(
                    $"System Error: Could not register Ctrl+0 hotkey (code {error}).\r\n\r\n");
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HotKeyId);
            _windowSource?.RemoveHook(WindowMessageHook);
        }

        private IntPtr WindowMessageHook(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
            {
                _ = SendPresetPromptAsync();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private async Task InitializeAgentAsync()
        {
            try
            {
                // 1. Read from Environment Variables
                string? clientId = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID");
                string? clientSecret = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_SECRET");

                // 2. Validate they exist
                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                {
                    ConversationTextBox.AppendText("System Error: SPOTIFY_CLIENT_ID or SPOTIFY_CLIENT_SECRET environment variables are missing.\r\n" +
                                                   "Please set them in your system or Visual Studio launch profiles.\r\n\r\n");
                    return; // Stop initialization
                }

                // 3. Initialize Agent
                _agent = await LocalAgent.CreateSpotifyAgentAsync(clientId, clientSecret);
                _agent.ToolCalled += Agent_ToolCalled;

                // 4. Re-enable UI
                PromptTextBox.IsEnabled = true;
                PresetPromptButton.IsEnabled = true;
                ConversationTextBox.AppendText("System: Spotify integration ready. Ask me to search for a song!\r\n\r\n");
                PromptTextBox.Focus();
            }
            catch (Exception ex)
            {
                ConversationTextBox.AppendText($"System Error: Could not initialize Spotify agent. {ex.Message}\r\n\r\n");
            }
        }

        private void Agent_ToolCalled(string toolName)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => Agent_ToolCalled(toolName));
                return;
            }

            ConversationTextBox.AppendText($"Tool used: {toolName}\r\n");
            ConversationTextBox.ScrollToEnd();
        }

        private async void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                await SendPromptAsync();
            }
        }

        private async void PresetPromptButton_Click(object sender, RoutedEventArgs e)
        {
            await SendPresetPromptAsync();
        }

        private async Task SendPresetPromptAsync()
        {
            await SendPromptAsync(PresetPrompt);
        }

        private async Task SendPromptAsync(string? promptOverride = null)
        {
            var prompt = (promptOverride ?? PromptTextBox.Text).Trim();
            if (string.IsNullOrEmpty(prompt) || _agent == null)
            {
                return;
            }
            PromptTextBox.Clear();
            PromptTextBox.Clear();
            ConversationTextBox.AppendText($"You: {prompt}\r\n");

            try
            {
                var answer = await _agent.SendAsync(prompt);
                ConversationTextBox.AppendText($"Assistant: {answer}\r\n\r\n");
                ConversationTextBox.ScrollToEnd();
            }
            catch (Exception exception)
            {
                ConversationTextBox.AppendText($"Error: {exception.Message}\r\n\r\n");
            }
            finally
            {
                PromptTextBox.Focus();
            }
        }

        private void ShowConversationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            var showConversation = ShowConversationCheckBox.IsChecked == true;
            ConversationTextBox.Visibility = showConversation
                ? Visibility.Visible
                : Visibility.Collapsed;
            ConversationRow.Height = showConversation
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
        }
    }

}