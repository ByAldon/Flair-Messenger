Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

appDir = fso.GetParentFolderName(WScript.ScriptFullName)
dataDir = fso.BuildPath(appDir, "data")
If Not fso.FolderExists(dataDir) Then
    fso.CreateFolder(dataDir)
End If

projectPath = fso.BuildPath(appDir, "src\FlairMessenger\FlairMessenger.csproj")
logPath = fso.BuildPath(dataDir, "launcher.log")

command = "cmd /c set ""FLAIR_MESSENGER_HOME=" & appDir & """ && cd /d """ & appDir & """ && dotnet run --project """ & projectPath & """ >> """ & logPath & """ 2>&1"
shell.Run command, 0, False
