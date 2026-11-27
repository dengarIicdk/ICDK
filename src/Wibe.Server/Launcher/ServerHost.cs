using System.Net;

namespace Wibe.Server.Launcher;

/// <summary>
/// Локальный сервер приложения как объект: окно лаунчера поднимает его на старте и гасит при
/// закрытии, а режим --headless-server крутит его без окна (так его гоняют автотесты).
/// Слушает только 127.0.0.1.
/// </summary>
internal sealed class ServerHost : IDisposable
{
    private const int FirstPort = 17890;
    private const int LastPort = 17990;

    private readonly HttpListener _listener;
    private volatile bool _stopped;

    private ServerHost(HttpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>Поднимает сервер на первом свободном порту, или null, если все заняты.</summary>
    public static ServerHost? Start()
    {
        var listener = new HttpListener();
        for (var candidate = FirstPort; candidate <= LastPort; candidate++)
        {
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                AppState.Instance.Start();
                var host = new ServerHost(listener, candidate);
                host.Accept();
                return host;
            }
            catch (HttpListenerException)
            {
                // порт занят — пробуем следующий
            }
        }

        return null;
    }

    private void Accept()
    {
        _ = Task.Run(async () =>
        {
            while (!_stopped)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }

                _ = Task.Run(() => Program.HandleAsync(context));
            }
        });
    }

    /// <summary>Блокирующее ожидание: для режима без окна.</summary>
    public void RunBlocking()
    {
        while (!_stopped)
        {
            try
            {
                var context = _listener.GetContext();
                _ = Task.Run(() => Program.HandleAsync(context));
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // уже закрыт
        }
    }
}
