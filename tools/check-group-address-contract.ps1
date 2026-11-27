param([string]$EtsPath = 'C:\Program Files (x86)\ETS6')
$ErrorActionPreference = 'Stop'

# Offline SDK conversion only: no Root, bus connection, read or write is created.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public static class OfflineEtsResolver
{
    public static string DirectoryPath;
    private static readonly HashSet<string> Active = new HashSet<string>();
    public static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            if (loaded.GetName().Name == name) return loaded;
        lock (Active)
        {
            if (!Active.Add(name)) return null;
        }
        try
        {
            string path = Path.Combine(DirectoryPath, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        finally
        {
            lock (Active) Active.Remove(name);
        }
    }
}
'@
[OfflineEtsResolver]::DirectoryPath = $EtsPath
$resolve = [Delegate]::CreateDelegate([ResolveEventHandler], [OfflineEtsResolver].GetMethod('Resolve'))
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolve)
try {
    $sdk = [Reflection.Assembly]::LoadFrom((Join-Path $EtsPath 'Knx.Ets.Sdk.dll'))
    $commType = $sdk.GetType('Knx.Ets.Sdk.Network.KnxCommunicationSync', $true)
    $gaType = $sdk.GetType('Knx.Ets.Sdk.Project.GroupAddress', $true)
    $comm = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($commType)
    $dom = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($gaType)
    $method = $commType.GetMethod('ConvertFromComFalconGroupAddress', [Reflection.BindingFlags]'Instance,NonPublic')
    if (!$method) { throw 'This SDK does not expose the conversion method under test.' }
    $bad = $null
    try {
        $bad = $method.Invoke($comm, [object[]]@($dom))
    }
    catch {
        if (!($_.Exception.GetBaseException() -is [ArgumentException])) { throw }
        # SDK builds may throw on an unsupported address instead of returning null.
    }
    $numeric = $method.Invoke($comm, [object[]]@([uint16]11010))
    $text = $method.Invoke($comm, [object[]]@('5/3/2'))
    if ($null -ne $bad) { throw 'SDK behavior differs: a project DOM object was accepted.' }
    if ($null -eq $numeric -or $null -eq $text) { throw 'SDK rejected a supported numeric/string address.' }
    if (!$numeric.Equals($text)) { throw 'Numeric and string addresses resolved differently.' }
    Write-Output 'PASS: project DOM object rejected; UInt16/string accepted and resolve to the same destination.'
    Write-Output 'No network connection or bus operation was performed.'
}
finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolve)
}
