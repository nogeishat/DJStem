param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$Icon)
$ErrorActionPreference = 'Stop'
if (!('DJStemIconResources' -as [type])) { Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DJStemIconResources {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern IntPtr BeginUpdateResource(string file,bool delete);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool UpdateResource(IntPtr handle,IntPtr type,IntPtr name,ushort language,byte[] data,uint size);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool EndUpdateResource(IntPtr handle,bool discard);
}
'@ }
$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Icon))
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($bytes)); $reader.ReadUInt16() | Out-Null; $reader.ReadUInt16() | Out-Null; $count = $reader.ReadUInt16()
$groupStream = [IO.MemoryStream]::new(); $group = [IO.BinaryWriter]::new($groupStream); $group.Write([uint16]0); $group.Write([uint16]1); $group.Write([uint16]$count)
$handle = [DJStemIconResources]::BeginUpdateResource((Resolve-Path -LiteralPath $Executable),$false)
if ($handle -eq [IntPtr]::Zero) { throw 'Could not open executable icon resources.' }
try {
    for ($index=0; $index -lt $count; $index++) {
        $entry = $reader.ReadBytes(12); $offset = $reader.ReadUInt32(); $length = [BitConverter]::ToUInt32($entry,8)
        $data = [byte[]]::new($length); [Array]::Copy($bytes,$offset,$data,0,$length)
        if (![DJStemIconResources]::UpdateResource($handle,[IntPtr]3,[IntPtr]($index+1),0,$data,$length)) { throw 'Could not update an icon image.' }
        $group.Write($entry); $group.Write([uint16]($index+1))
    }
    $data = $groupStream.ToArray()
    if (![DJStemIconResources]::UpdateResource($handle,[IntPtr]14,[IntPtr]1,0,$data,$data.Length)) { throw 'Could not update the icon group.' }
    if (![DJStemIconResources]::EndUpdateResource($handle,$false)) { throw 'Could not save executable icon.' }; $handle = [IntPtr]::Zero
} finally { if ($handle -ne [IntPtr]::Zero) { [DJStemIconResources]::EndUpdateResource($handle,$true) | Out-Null }; $reader.Dispose(); $group.Dispose(); $groupStream.Dispose() }
