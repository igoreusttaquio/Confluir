namespace Confluir.Core.Settings;

public interface ISettingsStore
{
    AppSettings Current { get; }

    void Save();
}
