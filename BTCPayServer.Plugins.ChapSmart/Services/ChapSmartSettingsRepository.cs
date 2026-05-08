using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Services.Stores;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ChapSmart.Services;

/// <summary>
/// Stores ChapSmart settings per-store using BTCPay's StoreRepository
/// Settings are stored as JSON blobs in the store's settings
/// </summary>
public class ChapSmartSettingsRepository
{
    private readonly StoreRepository _storeRepository;
    private const string SettingsKey = "ChapSmart";

    public ChapSmartSettingsRepository(StoreRepository storeRepository)
    {
        _storeRepository = storeRepository;
    }

    public async Task<ChapSmartSettings> GetSettings(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store == null) return null;

        var raw = store.GetStoreBlob()?.AdditionalData;
        if (raw == null || !raw.ContainsKey(SettingsKey)) return null;

        try
        {
            return raw[SettingsKey].ToObject<ChapSmartSettings>();
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveSettings(string storeId, ChapSmartSettings settings)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store == null) return;

        settings.StoreId = storeId;
        var blob = store.GetStoreBlob();
        blob.AdditionalData ??= new System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JToken>();
        blob.AdditionalData[SettingsKey] = Newtonsoft.Json.Linq.JToken.FromObject(settings);
        store.SetStoreBlob(blob);
        await _storeRepository.UpdateStore(store);
    }
}
