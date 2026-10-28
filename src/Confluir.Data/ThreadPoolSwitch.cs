using System.Runtime.CompilerServices;

namespace Confluir.Data;

/// <summary>
/// <c>await ThreadPoolSwitch.Instance</c> continua o método numa thread do pool. O Microsoft.Data.Sqlite executa as
/// consultas de forma síncrona (mesmo nos métodos *Async); sem isso, cada leitura feita a partir da interface
/// travaria a tela enquanto o banco trabalha. Quem chamou continua no próprio contexto ao receber o resultado.
/// </summary>
internal readonly struct ThreadPoolSwitch : INotifyCompletion
{
    public static ThreadPoolSwitch Instance => default;

    public ThreadPoolSwitch GetAwaiter() => this;

    public bool IsCompleted => Thread.CurrentThread.IsThreadPoolThread && SynchronizationContext.Current is null;

    public void OnCompleted(Action continuation) =>
        ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), continuation);

    public void GetResult()
    {
    }
}
