using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TradingLauncher
{
    // Shared UTF-8/ASCII-JSON transport for read-only history and Codex work.
    public sealed class ReviewWorker : IDisposable
    {
        private Process process;
        private bool disposed;
        public bool IsBusy { get { return process != null; } }
        public static string IdentifyAccount(string root, string fallback)
        {
            if (!File.Exists(CredentialStore.FilePath(root))) return fallback;
            var credentials = CredentialStore.Load(root);
            try
            {
                using (SHA256 hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes((string)credentials["key"]))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
            }
            finally { credentials.Clear(); }
        }
        public static string SerializeRequest(Dictionary<string, object> request)
        {
            string serialized = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(request);
            var wire = new StringBuilder(serialized.Length);
            foreach (char character in serialized)
                if (character > 127) wire.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else wire.Append(character);
            return wire.ToString();
        }
        public static ProcessStartInfo CreateStartInfo(string root)
        {
            return new ProcessStartInfo(Path.Combine(root, ".venv", "Scripts", "python.exe"), "-B -u -X utf8 -m trading.review") {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
        }
        public async Task<int> RunAsync(string root, Dictionary<string, object> request, Action<string> receive, Action started)
        {
            if (disposed) throw new ObjectDisposedException("ReviewWorker");
            if (IsBusy) throw new InvalidOperationException("이미 처리 중입니다.");
            var child = new Process { StartInfo = CreateStartInfo(root) };
            process = child;
            try
            {
                if (!child.Start()) throw new InvalidOperationException("거래 내역 프로세스를 실행하지 못했습니다.");
                child.StandardInput.WriteLine(SerializeRequest(request)); child.StandardInput.Flush(); request.Clear();
                if (started != null) started();
                Task<string> errors = child.StandardError.ReadToEndAsync();
                string line;
                while ((line = await child.StandardOutput.ReadLineAsync()) != null) if (!disposed) receive(line);
                await Task.Run(delegate { child.WaitForExit(); });
                await errors; // Drain stderr without exposing credentials or raw subprocess output.
                return child.ExitCode;
            }
            finally
            {
                request.Clear();
                try
                {
                    if (!child.HasExited)
                    {
                        Cancel();
                        if (!child.WaitForExit(2000)) child.Kill();
                    }
                }
                catch (InvalidOperationException) { }
                child.Dispose(); process = null;
            }
        }
        public void Cancel()
        {
            if (process == null) return;
            try { process.StandardInput.WriteLine("{\"action\":\"cancel\"}"); process.StandardInput.Flush(); }
            catch (InvalidOperationException) { } catch (IOException) { }
        }
        public void Dispose() { disposed = true; Cancel(); }
    }
}
