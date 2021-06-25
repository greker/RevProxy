using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;

[Route("{tenant}"), ApiController]
public class ConfigController : ControllerBase
{
    static readonly ConcurrentDictionary<string, TenantInfo> info = new();

    [HttpGet("config")] public ICollection<RecInfo> Get(string tenant) => GetCurrentTenant(tenant).Funcs.Values;
    [HttpPost("config/emu")] public void Post([FromBody] MethodInfo value, string tenant) => MapMethod(tenant, value);
    [HttpPost("config/proxy")] public void PostProxy([FromBody] ProxyInfo value, string tenant) => MapMethod(tenant, value);
    [HttpDelete("config")] public void Delete(string tenant) => GetCurrentTenant(tenant).Funcs.Clear();

    internal static TenantInfo GetCurrentTenant(string tenant) => info.GetOrAdd(tenant, (t) => new TenantInfo(new ConcurrentDictionary<string, RecInfo>()));

    static void MapMethod(string tenant, RecInfo res)
    {
        if (!string.IsNullOrEmpty(res.Name) && res.Name != "config" && !res.Name.StartsWith("config/"))
            GetCurrentTenant(tenant).Funcs.AddOrUpdate(res.Id, res, (s, d) => res);
    }
}
