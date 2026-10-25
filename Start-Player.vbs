Option Explicit
Dim shell, files, root, command
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root
command = "node.exe " & Chr(34) & root & "\scripts\start-player.mjs" & Chr(34)
On Error Resume Next
shell.Run command, 0, False
If Err.Number <> 0 Then MsgBox "StemKit Player needs Node.js 22 or newer. Install it from nodejs.org and try again.", vbExclamation, "StemKit Player"
