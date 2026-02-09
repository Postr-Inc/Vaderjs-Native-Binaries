using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Security.Credentials;

namespace App1
{
    // ==================== Logger ====================
    public static class Logger
    {
        private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "app.log");
        private static readonly object _lock = new object();

        public static void Log(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                lock (_lock)
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch { }
        }

        public static void LogException(Exception ex, string context = "")
        {
            Log($"ERROR in {context}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ==================== AutoSecureStorage ====================
    public static class AutoSecureStorage
    {
        private static readonly string _storagePath;
        private static readonly object _fileLock = new object();
        private static byte[]? _deviceKey = null;

        static AutoSecureStorage()
        {
            _storagePath = Path.Combine(AppContext.BaseDirectory, "secure", "auto_vault.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            
            // Initialize device key on first access
            InitializeDeviceKey();
        }

        // ===== DEVICE KEY GENERATION =====
        private static void InitializeDeviceKey()
        {
            try
            {
                // Try Windows Credential Manager first (most secure)
                if (TryGetCredentialManagerKey(out var key))
                {
                    _deviceKey = key;
                    return;
                }

                // Fallback: Generate a stable device-specific key
                string deviceId = GetDeviceId();
                using var sha = SHA256.Create();
                _deviceKey = sha.ComputeHash(Encoding.UTF8.GetBytes($"VaderJS-DeviceKey-{deviceId}"));
                
                // Store in Credential Manager for future use
                StoreInCredentialManager(_deviceKey);
            }
            catch
            {
                // Ultimate fallback: app-specific but less secure
                using var sha = SHA256.Create();
                _deviceKey = sha.ComputeHash(Encoding.UTF8.GetBytes("VaderJS-Fallback-Key"));
            }
        }

        private static string GetDeviceId()
        {
            // Combine multiple machine identifiers for uniqueness
            var identifiers = new List<string>
            {
                Environment.MachineName,
                Environment.UserName,
                Environment.OSVersion.VersionString,
                GetMachineGuid()
            };
            
            string combined = string.Join("-", identifiers);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
            return Convert.ToBase64String(hash);
        }

        private static string GetMachineGuid()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string path = @"SOFTWARE\Microsoft\Cryptography";
                    string name = "MachineGuid";
                    
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    return key?.GetValue(name)?.ToString() ?? "default-guid";
                }
                return "non-windows-guid";
            }
            catch
            {
                return "error-guid";
            }
        }

        private static bool TryGetCredentialManagerKey(out byte[] key)
        {
            key = null;
            try
            {
                var vault = new PasswordVault();
                var creds = vault.FindAllByResource("VaderJS_DeviceKey");
                if (creds.Count > 0)
                {
                    var cred = vault.Retrieve("VaderJS_DeviceKey", "device_key");
                    key = Convert.FromBase64String(cred.Password);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void StoreInCredentialManager(byte[] key)
        {
            try
            {
                var vault = new PasswordVault();
                var cred = new PasswordCredential(
                    "VaderJS_DeviceKey",
                    "device_key",
                    Convert.ToBase64String(key)
                );
                vault.Add(cred);
            }
            catch { }
        }

        // ===== ENCRYPTION METHODS =====
        public static string Encrypt(string plaintext)
        {
            if (_deviceKey == null) throw new InvalidOperationException("Device key not initialized");
            
            using var aes = Aes.Create();
            
            // Use device key as the base, derive actual encryption key
            aes.Key = DeriveEncryptionKey(_deviceKey);
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            
            ms.Write(aes.IV);
            
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(plaintext);
            }

            return Convert.ToBase64String(ms.ToArray());
        }

        public static string Decrypt(string ciphertext)
        {
            if (_deviceKey == null) throw new InvalidOperationException("Device key not initialized");
            
            byte[] data = Convert.FromBase64String(ciphertext);
            
            using var aes = Aes.Create();
            aes.Key = DeriveEncryptionKey(_deviceKey);
            
            byte[] iv = new byte[aes.IV.Length];
            Array.Copy(data, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(data, iv.Length, data.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);
            
            return sr.ReadToEnd();
        }

        private static byte[] DeriveEncryptionKey(byte[] deviceKey)
        {
            // Add some salt to make it unique per app
            byte[] salt = Encoding.UTF8.GetBytes("VaderJS-Encryption-Salt");
            
            using var pbkdf2 = new Rfc2898DeriveBytes(
                deviceKey,
                salt,
                10000,
                HashAlgorithmName.SHA256
            );
            return pbkdf2.GetBytes(32); // 256-bit key
        }

        // ===== STORAGE OPERATIONS =====
        public static void Set(string key, string value)
        {
            lock (_fileLock)
            {
                var store = LoadStore();
                string encrypted = Encrypt(value);
                store[key] = encrypted;
                SaveStore(store);
            }
        }

        public static string? Get(string key)
        {
            lock (_fileLock)
            {
                var store = LoadStore();
                if (store.TryGetValue(key, out string encrypted))
                {
                    try
                    {
                        return Decrypt(encrypted);
                    }
                    catch (CryptographicException)
                    {
                        return null; // Corrupted or tampered data
                    }
                }
                return null;
            }
        }

        public static bool Delete(string key)
        {
            lock (_fileLock)
            {
                var store = LoadStore();
                bool removed = store.Remove(key);
                if (removed)
                {
                    SaveStore(store);
                }
                return removed;
            }
        }

        public static void Clear()
        {
            lock (_fileLock)
            {
                SaveStore(new Dictionary<string, string>());
            }
        }

        public static Dictionary<string, string> GetAll()
        {
            lock (_fileLock)
            {
                var store = LoadStore();
                var result = new Dictionary<string, string>();
                
                foreach (var kvp in store)
                {
                    try
                    {
                        result[kvp.Key] = Decrypt(kvp.Value);
                    }
                    catch { } // Skip corrupted entries
                }
                
                return result;
            }
        }

        // ===== PRIVATE STORAGE METHODS =====
        private static Dictionary<string, string> LoadStore()
        {
            if (!File.Exists(_storagePath))
                return new Dictionary<string, string>();

            try
            {
                string json = File.ReadAllText(_storagePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private static void SaveStore(Dictionary<string, string> store)
        {
            string json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }

        // ===== PUBLIC API FOR WEB =====
        public static bool IsAvailable()
        {
            return _deviceKey != null;
        }
    }

    // ==================== Main Window ====================
    public sealed partial class MainWindow : Window
    {
        private readonly string _allowedBaseDirectory = AppContext.BaseDirectory;
        private readonly string _allowedDataDirectory;
        private readonly HttpClient _httpClient = new();
        private string WebViewUrl = $"file:///{Path.Combine(AppContext.BaseDirectory, "Web", "index.html").Replace("\\", "/")}";
        private Dictionary<string, object> _memoryStore = new();

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "VaderJS Social";
            this.ExtendsContentIntoTitleBar = true;

            _allowedDataDirectory = Path.Combine(_allowedBaseDirectory, "WebData");
            Directory.CreateDirectory(_allowedDataDirectory);

            Logger.Log("=== VaderJS Social Started ===");
            Logger.Log($"AutoSecure Available: {AutoSecureStorage.IsAvailable()}");

            MyWebView.Source = new Uri(WebViewUrl);
            InitializeWebView();
        } 
        private async void InitializeWebView()
        {
            try
            {
                await MyWebView.EnsureCoreWebView2Async();
                MyWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                
                MyWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                Logger.Log("WebView2 initialized and ready for messages");
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "InitializeWebView");
            }
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string requestId = "";
            try
            {
                string jsonString = args.WebMessageAsJson;
                Logger.Log($"Received: {jsonString}");

                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;

                if (!root.TryGetProperty("command", out var commandElement) ||
                    !root.TryGetProperty("id", out var idElement))
                {
                    return;
                }

                string command = commandElement.GetString() ?? "";
                requestId = idElement.GetString() ?? "";

                switch (command)
                {
                    case "writeFile": await HandleWriteFile(requestId, root); break;
                    case "readFile": await HandleReadFile(requestId, root); break;
                    case "deleteFile": await HandleDeleteFile(requestId, root); break;
                    case "listDir": await HandleListDir(requestId, root); break;
                    case "setWindowSize": HandleSetWindowSize(requestId, root); break;
                    case "openExternal": HandleOpenExternal(requestId, root); break;
                    case "showInFolder": HandleShowInFolder(requestId, root); break;
                    case "http": await HandleHttpRequest(requestId, root); break;
                    case "memory": HandleMemoryCommands(requestId, root); break;
                    case "secureStore": await HandleSecureStoreCommands(requestId, root); break; // AUTO-SECURE!
                    case "ping": SendResponse(requestId, "pong"); break;
                    default: SendErrorResponse(requestId, $"Unknown command: {command}"); break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "OnWebMessageReceived");
                SendErrorResponse(requestId, $"Internal error: {ex.Message}");
            }
        }

        #region File Commands (same as before)
        private async Task HandleWriteFile(string requestId, JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("path", out var pathElement) || !root.TryGetProperty("content", out var contentElement))
                {
                    SendErrorResponse(requestId, "Missing path or content");
                    return;
                }

                string requestedPath = Path.GetFileName(pathElement.GetString() ?? "");
                string content = contentElement.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.Contains("..") || requestedPath.Contains("~"))
                {
                    SendErrorResponse(requestId, "Path invalid or not allowed");
                    return;
                }

                string safePath = Path.Combine(_allowedDataDirectory, requestedPath);
                Directory.CreateDirectory(Path.GetDirectoryName(safePath) ?? _allowedDataDirectory);

                await File.WriteAllTextAsync(safePath, content);
                SendResponse(requestId, true);
            }
            catch (Exception ex) { SendErrorResponse(requestId, $"Write failed: {ex.Message}"); }
        }

        private async Task HandleReadFile(string requestId, JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("path", out var pathElement)) { SendErrorResponse(requestId, "Missing path"); return; }

                string requestedPath = Path.GetFileName(pathElement.GetString() ?? "");
                string safePath = Path.Combine(_allowedDataDirectory, requestedPath);

                if (!File.Exists(safePath)) { SendResponse(requestId, "FILE_NOT_FOUND"); return; }

                string content = await File.ReadAllTextAsync(safePath);
                SendResponse(requestId, content);
            }
            catch (Exception ex) { SendErrorResponse(requestId, $"Read failed: {ex.Message}"); }
        }

        private async Task HandleDeleteFile(string requestId, JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("path", out var pathElement)) { SendErrorResponse(requestId, "Missing path"); return; }

                string requestedPath = Path.GetFileName(pathElement.GetString() ?? "");
                string safePath = Path.Combine(_allowedDataDirectory, requestedPath);

                if (File.Exists(safePath)) { File.Delete(safePath); SendResponse(requestId, true); }
                else SendResponse(requestId, false);
            }
            catch (Exception ex) { SendErrorResponse(requestId, $"Delete failed: {ex.Message}"); }
        }

        private async Task HandleListDir(string requestId, JsonElement root)
        {
            try
            {
                string requestedPath = root.TryGetProperty("path", out var pathElement) ? Path.GetFileName(pathElement.GetString() ?? "") : "";
                string safePath = Path.Combine(_allowedDataDirectory, requestedPath);

                if (!Directory.Exists(safePath)) { SendResponse(requestId, Array.Empty<string>()); return; }

                var files = Directory.GetFiles(safePath).Select(Path.GetFileName).ToArray();
                SendResponse(requestId, files);
            }
            catch (Exception ex) { SendErrorResponse(requestId, $"List failed: {ex.Message}"); }
        }
        #endregion

        #region Memory Commands
        private void HandleMemoryCommands(string requestId, JsonElement root)
        {
            try
            {
                string subcommand = root.GetProperty("subcommand").GetString() ?? "";
                
                switch (subcommand)
                {
                    case "set":
                        _memoryStore[root.GetProperty("key").GetString() ?? ""] = root.GetProperty("value").GetRawText();
                        SendResponse(requestId, true);
                        break;
                    case "get":
                        _memoryStore.TryGetValue(root.GetProperty("key").GetString() ?? "", out var value);
                        SendResponse(requestId, value);
                        break;
                    case "delete":
                        _memoryStore.Remove(root.GetProperty("key").GetString() ?? "");
                        SendResponse(requestId, true);
                        break;
                    case "clear":
                        _memoryStore.Clear();
                        SendResponse(requestId, true);
                        break;
                    default:
                        SendErrorResponse(requestId, $"Unknown memory command: {subcommand}");
                        break;
                }
            }
            catch (Exception ex)
            {
                SendErrorResponse(requestId, $"Memory command failed: {ex.Message}");
            }
        }
        #endregion

        #region Auto-SecureStore Commands - NO PASSWORD REQUIRED!
        private async Task HandleSecureStoreCommands(string requestId, JsonElement root)
        {
            try
            {
                if (!AutoSecureStorage.IsAvailable())
                {
                    SendErrorResponse(requestId, "Secure storage not available on this device");
                    return;
                }

                string subcommand = root.GetProperty("subcommand").GetString() ?? "";
                
                switch (subcommand)
                {
                    case "set":
{
    string key = root.GetProperty("key").GetString() ?? "";
    
    // Serialize value to JSON string if it's not already a string
    var valueElement = root.GetProperty("value");
    string value = valueElement.ValueKind == JsonValueKind.String
        ? valueElement.GetString()!
        : valueElement.GetRawText(); // converts object/array to JSON string

    AutoSecureStorage.Set(key, value);
    SendResponse(requestId, true);
    break;
}
                    case "get":
                        {
                            string key = root.GetProperty("key").GetString() ?? "";
                            string? value = AutoSecureStorage.Get(key);
                            SendResponse(requestId, value);
                            break;
                        }
                    case "delete":
                        {
                            string key = root.GetProperty("key").GetString() ?? "";
                            bool deleted = AutoSecureStorage.Delete(key);
                            SendResponse(requestId, deleted);
                            break;
                        }
                    case "clear":
                        AutoSecureStorage.Clear();
                        SendResponse(requestId, true);
                        break;
                    case "getAll":
                        var allValues = AutoSecureStorage.GetAll();
                        SendResponse(requestId, allValues);
                        break;
                    case "isAvailable":
                        SendResponse(requestId, AutoSecureStorage.IsAvailable());
                        break;
                    default:
                        SendErrorResponse(requestId, $"Unknown secureStore command: {subcommand}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "SecureStoreCommands");
                SendErrorResponse(requestId, $"Secure storage error: {ex.Message}");
            }
        }
        #endregion

        #region HTTP Requests
        private async Task HandleHttpRequest(string requestId, JsonElement root)
        {
            try
            {
                string url = root.GetProperty("url").GetString()!;
                string method = root.GetProperty("method").GetString() ?? "GET";

                var req = new HttpRequestMessage(new HttpMethod(method), url);

                if (root.TryGetProperty("headers", out var headers))
                    foreach (var h in headers.EnumerateObject())
                        req.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString());

                if (root.TryGetProperty("body", out var body))
                    req.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

                var res = await _httpClient.SendAsync(req);
                string content = await res.Content.ReadAsStringAsync();

                SendResponse(requestId, new { status = (int)res.StatusCode, body = content });
            }
            catch (Exception ex)
            {
                SendErrorResponse(requestId, $"HTTP request failed: {ex.Message}");
            }
        }
        #endregion

        #region Window & System Commands
        private void HandleSetWindowSize(string requestId, JsonElement root)
        {
            try
            {
                int width = root.GetProperty("width").GetInt32();
                int height = root.GetProperty("height").GetInt32();
                this.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
                SendResponse(requestId, true);
            }
            catch (Exception ex)
            {
                SendErrorResponse(requestId, $"Failed to resize window: {ex.Message}");
            }
        }

        private void HandleOpenExternal(string requestId, JsonElement root)
        {
            try
            {
                string? url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                if (!string.IsNullOrEmpty(url))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    SendResponse(requestId, true);
                }
                else SendErrorResponse(requestId, "URL is null or empty");
            }
            catch (Exception ex)
            {
                SendErrorResponse(requestId, $"Failed to open URL: {ex.Message}");
            }
        }

        private void HandleShowInFolder(string requestId, JsonElement root)
        {
            try
            {
                string? path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                    SendResponse(requestId, true);
                }
                else SendErrorResponse(requestId, "Path is null, empty, or doesn't exist");
            }
            catch (Exception ex)
            {
                SendErrorResponse(requestId, $"Failed to show in folder: {ex.Message}");
            }
        }
        #endregion

        private void SendResponse(string id, object payload)
        {
            try
            {
                var response = new { id = id, data = payload };
                string jsonResponse = JsonSerializer.Serialize(response);
                MyWebView?.CoreWebView2?.PostWebMessageAsJson(jsonResponse);
            }
            catch (Exception ex) { Logger.LogException(ex, "SendResponse"); }
        }

        private void SendErrorResponse(string id, string error)
        {
            try
            {
                var response = new { id = id, error = error };
                string jsonResponse = JsonSerializer.Serialize(response);
                MyWebView?.CoreWebView2?.PostWebMessageAsJson(jsonResponse);
            }
            catch (Exception ex) { Logger.LogException(ex, "SendErrorResponse"); }
        }
    }
}