Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

appDir = fso.GetParentFolderName(WScript.ScriptFullName)
dataDir = fso.BuildPath(appDir, "data")
If Not fso.FolderExists(dataDir) Then
    fso.CreateFolder(dataDir)
End If

projectPath = fso.BuildPath(appDir, "src\FlairMessenger\FlairMessenger.csproj")
appPath = fso.BuildPath(appDir, "app\FlairMessenger.dll")
logPath = fso.BuildPath(dataDir, "launcher.log")

If fso.FileExists(appPath) Then
    runTarget = "dotnet """ & appPath & """"
Else
    runTarget = "dotnet run --project """ & projectPath & """"
End If

command = "cmd /c set ""FLAIR_MESSENGER_HOME=" & appDir & """ && cd /d """ & appDir & """ && " & runTarget & " >> """ & logPath & """ 2>&1"
shell.Run command, 0, False
