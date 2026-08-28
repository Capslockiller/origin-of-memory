; yazan: codex
; model: gpt-5.6-sol
; Native, per-user installer for origin-of-memory.

#define AppName "Origin of Memory"
#define AppVersion "0.3.0"
#define AppPublisher "origin-of-memory project"
#define AppURL "https://github.com/Capslockiller/origin-of-memory"

[Setup]
AppId={{8D50B87E-88B8-4B75-A0B3-84EA905AF823}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Origin of Memory
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableWelcomePage=no
DisableReadyPage=no
DisableReadyMemo=no
PrivilegesRequired=lowest
Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={sys}\WindowsPowerShell\v1.0\powershell.exe
OutputDir=output
OutputBaseFilename=Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no

[Files]
; Probe copies are extracted before installation. Their code is still owned by
; kur.ps1 and the existing Python decision modules.
; dontcopy ignores DestDir: every one of these lands flat in {tmp}. The probe
; tree kur.ps1 expects is assembled in PrepareProbeTree instead.
Source: "..\kur.ps1"; Flags: dontcopy noencryption
Source: "..\scripts\donanim.py"; Flags: dontcopy noencryption
Source: "..\scripts\model_oneri.py"; Flags: dontcopy noencryption
Source: "..\scripts\kurulum_plani.py"; Flags: dontcopy noencryption

Source: "..\kur.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\*.py"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\hooks\*"; DestDir: "{app}\hooks"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\skills\*"; DestDir: "{app}\skills"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\template\*"; DestDir: "{app}\template"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\install.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\setup-wizard.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\installer.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\Origin of Memory Setup"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\kur.ps1"""; WorkingDir: "{app}"; Comment: "Configure Origin of Memory"; Check: ShouldCreateDesktopShortcut

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -Answers ""{app}\uninstall-plan.json"""; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "OriginOfMemoryCleanup"

[Code]
const
  PythonURL64 = 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe';
  PythonURL32 = 'https://www.python.org/ftp/python/3.12.10/python-3.12.10.exe';
  PythonInstallerName = 'python-3.12.10-installer.exe';

var
  SystemPage: TOutputMsgMemoWizardPage;
  PythonPage: TInputOptionWizardPage;
  VaultPage: TInputDirWizardPage;
  PresetPage: TInputOptionWizardPage;
  ModelPage: TInputOptionWizardPage;
  DetectionProgressPage: TOutputProgressWizardPage;
  PythonProgressPage: TOutputProgressWizardPage;
  DesktopShortcutCheck: TNewCheckBox;
  HowItWorksCheck: TNewCheckBox;
  DetectionIni: String;
  PlanPath: String;
  PythonOK: Boolean;
  WingetPresent: Boolean;
  OllamaPresent: Boolean;
  DetectionComplete: Boolean;
  ModelsLoaded: Boolean;
  InstallSucceeded: Boolean;
  PythonRemediated: Boolean;
  DocumentsRedirected: Boolean;
  RecommendedPreset: String;
  LocalModelBackend: String;
  VaultReason: String;
  ModelCount: Integer;

function PSExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function Q(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function JsonQuote(const Value: String): String;
var
  S: String;
begin
  S := Value;
  StringChangeEx(S, '\', '\\', True);
  StringChangeEx(S, '"', '\"', True);
  StringChangeEx(S, #13#10, '\n', True);
  StringChangeEx(S, #13, '\n', True);
  StringChangeEx(S, #10, '\n', True);
  Result := '"' + S + '"';
end;

function JsonBool(const Value: Boolean): String;
begin
  if Value then Result := 'true'
  else Result := 'false';
end;

function SaveUTF8Text(const FileName, Text: String): Boolean;
var
  Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  Result := SaveStringsToUTF8FileWithoutBOM(FileName, Lines, False);
end;

function IniValue(const Section, Key, Default: String): String;
begin
  Result := GetIniString(Section, Key, Default, DetectionIni);
end;

function IniBool(const Section, Key: String): Boolean;
var
  Value: String;
begin
  Value := Lowercase(IniValue(Section, Key, '0'));
  Result := (Value = '1') or (Value = 'true');
end;

function RecommendedSuffix(const Preset: String): String;
begin
  if RecommendedPreset = Preset then Result := ' (Recommended)'
  else Result := '';
end;

function BuildDetectionScript: String;
begin
  Result :=
    'param([string]$Root,[string]$Out)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '$ProgressPreference = ''SilentlyContinue''' + #13#10 +
    '$machinePath = [Environment]::GetEnvironmentVariable(''Path'',''Machine'')' + #13#10 +
    '$userPath = [Environment]::GetEnvironmentVariable(''Path'',''User'')' + #13#10 +
    '$env:Path = @($machinePath,$userPath) -join '';''' + #13#10 +
    '$lines = New-Object Collections.Generic.List[string]' + #13#10 +
    'function Add-Line([string]$Name,[object]$Value) {' + #13#10 +
    '  $text = ([string]$Value).Replace("`r",'' '').Replace("`n",'' '')' + #13#10 +
    '  $lines.Add("$Name=$text")' + #13#10 +
    '}' + #13#10 +
    '$source = [IO.File]::ReadAllText((Join-Path $Root ''kur.ps1''))' + #13#10 +
    '$entry = [regex]::Match($source, ''(?m)^try \{\r?\n  if \(\$Answers -and \$Recommended\)'')' + #13#10 +
    'if (-not $entry.Success) { throw ''kur.ps1 entry point was not found'' }' + #13#10 +
    'Invoke-Expression $source.Substring(0,$entry.Index)' + #13#10 +
    '$script:RepoRoot = $Root' + #13#10 +
    '$lines.Add(''[probe]'')' + #13#10 +
    'Add-Line ''WingetPresent'' ([bool](Get-Command winget -ErrorAction SilentlyContinue))' + #13#10 +
    'try { $python = Find-PythonCommand } catch {' + #13#10 +
    '  Add-Line ''PythonState'' ''missing''' + #13#10 +
    '  Add-Line ''PythonVersion'' ''Not found''' + #13#10 +
    '  $lines.Add(''[plan]'')' + #13#10 +
    '  Add-Line ''VaultDefault'' (Join-Path $env:USERPROFILE ''brain'')' + #13#10 +
    '  Add-Line ''VaultReason'' ''Python is unavailable, so the user profile is used to avoid a cloud-synced default.''' + #13#10 +
    '  $lines.Add(''[models]''); Add-Line ''Count'' 0' + #13#10 +
    '  [IO.File]::WriteAllLines($Out,$lines,[Text.UTF8Encoding]::new($false)); exit 0' + #13#10 +
    '}' + #13#10 +
    '$versionText = (& $python.Exe @($python.Prefix) --version 2>&1 | Out-String).Trim()' + #13#10 +
    '$versionExit = $LASTEXITCODE' + #13#10 +
    'Add-Line ''PythonVersion'' $versionText' + #13#10 +
    'if (($versionExit -ne 0) -or ($versionText -notmatch ''Python\s+(\d+)\.(\d+)'') -or ([int]$Matches[1] -lt 3) -or (([int]$Matches[1] -eq 3) -and ([int]$Matches[2] -lt 12))) {' + #13#10 +
    '  Add-Line ''PythonState'' ''wrong''' + #13#10 +
    '  $lines.Add(''[plan]'')' + #13#10 +
    '  Add-Line ''VaultDefault'' (Join-Path $env:USERPROFILE ''brain'')' + #13#10 +
    '  Add-Line ''VaultReason'' ''Python 3.12+ is unavailable, so the user profile is used to avoid a cloud-synced default.''' + #13#10 +
    '  $lines.Add(''[models]''); Add-Line ''Count'' 0' + #13#10 +
    '  [IO.File]::WriteAllLines($Out,$lines,[Text.UTF8Encoding]::new($false)); exit 0' + #13#10 +
    '}' + #13#10 +
    'Add-Line ''PythonState'' ''present''' + #13#10 +
    '$bundle = Get-AutoDecision' + #13#10 +
    '$context = $bundle.Context; $decision = $bundle.Decision' + #13#10 +
    'Add-Line ''ClaudePresent'' ([bool]$context.probe.commands.claude)' + #13#10 +
    'Add-Line ''OllamaPresent'' ([bool]$context.probe.commands.ollama)' + #13#10 +
    'Add-Line ''HardwarePresent'' ([bool](($null -ne $context.probe.ram_gb) -or $context.probe.cpu.name -or @($context.probe.gpus).Count))' + #13#10 +
    'Add-Line ''DocumentsPath'' $context.documents_path' + #13#10 +
    '$expectedDocuments = Join-Path $context.user_profile ''Documents''' + #13#10 +
    '$redirected = -not ([IO.Path]::GetFullPath($context.documents_path).TrimEnd(''\'').Equals([IO.Path]::GetFullPath($expectedDocuments).TrimEnd(''\''),[StringComparison]::OrdinalIgnoreCase))' + #13#10 +
    'Add-Line ''DocumentsRedirected'' $redirected' + #13#10 +
    'Add-Line ''McpPresent'' ([bool]$context.mcp_config_exists)' + #13#10 +
    '$detectedRuntimes = @($decision.detected_runtimes)' + #13#10 +
    'Add-Line ''RuntimeCount'' $detectedRuntimes.Count' + #13#10 +
    'Add-Line ''RuntimeNames'' (($detectedRuntimes | ForEach-Object { $_.name }) -join '', '')' + #13#10 +
    '$lines.Add(''[plan]'')' + #13#10 +
    'Add-Line ''RecommendedPreset'' $decision.plan.preset' + #13#10 +
    'Add-Line ''VaultDefault'' $decision.plan.vault' + #13#10 +
    'Add-Line ''VaultReason'' $decision.reasons.vault' + #13#10 +
    '$ollamaRows = @($decision.detected_runtimes | Where-Object { $_.name -match ''^(?i:ollama)$'' })' + #13#10 +
    '$localBundle = [pscustomobject]@{ Decision = [pscustomobject]@{ detected_runtimes = $ollamaRows } }' + #13#10 +
    'function Read-Choice([string]$Title,[object[]]$Options,[int]$DefaultIndex=0) { return $Options[$DefaultIndex].Value }' + #13#10 +
    '$runtimeChoice = Read-RuntimeChoice $localBundle ''local''' + #13#10 +
    'Add-Line ''ModelBackend'' $runtimeChoice.backend' + #13#10 +
    'Add-Line ''InstallRuntime'' ([bool]$runtimeChoice.install)' + #13#10 +
    '$lines.Add(''[models]'')' + #13#10 +
    '$recommendations = @($context.recommendations)' + #13#10 +
    'Add-Line ''Count'' $recommendations.Count' + #13#10 +
    'for ($i=0; $i -lt $recommendations.Count; $i++) {' + #13#10 +
    '  $n=$i+1; $item=$recommendations[$i]' + #13#10 +
    '  Add-Line (''Tag''+$n) $item.tag; Add-Line (''Size''+$n) $item.size_gb' + #13#10 +
    '  Add-Line (''Label''+$n) $item.label; Add-Line (''Why''+$n) $item.why' + #13#10 +
    '}' + #13#10 +
    '[IO.File]::WriteAllLines($Out,$lines,[Text.UTF8Encoding]::new($false))';
end;

procedure LoadModelChoices;
var
  I, Limit: Integer;
  Caption: String;
begin
  if ModelsLoaded then Exit;
  ModelCount := StrToIntDef(IniValue('models', 'Count', '0'), 0);
  Limit := ModelCount;
  if Limit > 3 then Limit := 3;
  for I := 1 to Limit do
  begin
    Caption := IniValue('models', 'Tag' + IntToStr(I), '') + '  (' +
      IniValue('models', 'Size' + IntToStr(I), '?') + ' GB, ' +
      IniValue('models', 'Label' + IntToStr(I), 'unknown') + ') - ' +
      IniValue('models', 'Why' + IntToStr(I), '');
    ModelPage.Add(Caption);
  end;
  if Limit > 0 then ModelPage.SelectedValueIndex := 0;
  ModelsLoaded := Limit > 0;
end;

procedure UpdateSystemPage;
var
  S, State, Consequence: String;
  ClaudePresent, HardwarePresent, McpPresent: Boolean;
  RuntimeCount: Integer;
begin
  PythonOK := IniValue('probe', 'PythonState', '') = 'present';
  WingetPresent := IniBool('probe', 'WingetPresent');
  S := '';
  if PythonOK then State := 'Present' else State := 'Required';
  S := S + 'Python 3.12+  -  ' + State + #13#10 +
    '  ' + IniValue('probe', 'PythonVersion', 'Not found') + #13#10#13#10;
  if not PythonOK then
  begin
    Consequence := 'Install Python to unlock hardware and model detection.';
    S := S + 'Claude Code  -  Unavailable' + #13#10 + '  ' + Consequence + #13#10#13#10;
    S := S + 'Local runtime  -  Unavailable' + #13#10 + '  ' + Consequence + #13#10#13#10;
    S := S + 'Hardware probe  -  Unavailable' + #13#10 + '  ' + Consequence + #13#10#13#10;
    S := S + 'Documents folder  -  Unavailable' + #13#10 +
      '  The safe default is your local user profile, not Documents.' + #13#10#13#10;
    S := S + 'Local model recommendation  -  Unavailable' + #13#10 + '  ' + Consequence;
  end
  else
  begin
    ClaudePresent := IniBool('probe', 'ClaudePresent');
    HardwarePresent := IniBool('probe', 'HardwarePresent');
    McpPresent := IniBool('probe', 'McpPresent');
    OllamaPresent := IniBool('probe', 'OllamaPresent');
    RuntimeCount := StrToIntDef(IniValue('probe', 'RuntimeCount', '0'), 0);
    if ClaudePresent then begin State := 'Present'; Consequence := 'Automatic capture and compilation are available.'; end
    else begin State := 'Optional'; Consequence := 'Cloud, hybrid, and local capture need Claude Code; setup can still stage files.'; end;
    S := S + 'Claude Code  -  ' + State + #13#10 + '  ' + Consequence + #13#10#13#10;
    if RuntimeCount > 0 then begin State := 'Present'; Consequence := IniValue('probe', 'RuntimeNames', '') + ' can serve local model work.'; end
    else begin State := 'Optional'; Consequence := 'Cloud needs no local runtime; a local preset will install one after consent.'; end;
    S := S + 'Local runtime  -  ' + State + #13#10 + '  ' + Consequence + #13#10#13#10;
    if HardwarePresent then begin State := 'Present'; Consequence := 'RAM, CPU, GPU, and model-store disk were inspected.'; end
    else begin State := 'Unavailable'; Consequence := 'Local-model fit could not be estimated.'; end;
    S := S + 'Hardware probe  -  ' + State + #13#10 + '  ' + Consequence + #13#10#13#10;
    if DocumentsRedirected then
      Consequence := 'Redirected/cloud-synced Documents is not used; the vault defaults to your user profile.'
    else
      Consequence := 'The normal local Documents folder is available.';
    S := S + 'Documents folder  -  Present' + #13#10 + '  ' + Consequence + #13#10#13#10;
    if ModelCount > 0 then begin State := 'Present'; Consequence := 'model_oneri.py ranked verified candidates for this hardware.'; end
    else begin State := 'Unavailable'; Consequence := 'No verified local model candidate is available.'; end;
    S := S + 'Local model recommendation  -  ' + State + #13#10 + '  ' + Consequence + #13#10#13#10;
    if McpPresent then begin State := 'Present'; Consequence := 'Claude Desktop memory access will be registered.'; end
    else begin State := 'Optional'; Consequence := 'Desktop integration stays off; core setup still works.'; end;
    S := S + 'Claude Desktop config  -  ' + State + #13#10 + '  ' + Consequence;
  end;
  SystemPage.RichEditViewer.Lines.Text := S;
end;

procedure ApplyDetection;
var
  PresetIndex: Integer;
begin
  PythonOK := IniValue('probe', 'PythonState', '') = 'present';
  ModelCount := StrToIntDef(IniValue('models', 'Count', '0'), 0);
  RecommendedPreset := IniValue('plan', 'RecommendedPreset', 'cloud');
  if RecommendedPreset = 'lite' then RecommendedPreset := 'local';
  if (ModelCount = 0) and (RecommendedPreset <> 'cloud') then RecommendedPreset := 'cloud';
  LocalModelBackend := IniValue('plan', 'ModelBackend', '');
  VaultReason := IniValue('plan', 'VaultReason', 'The user profile avoids a cloud-synced default.');
  DocumentsRedirected := IniBool('probe', 'DocumentsRedirected');
  VaultPage.Values[0] := IniValue('plan', 'VaultDefault', ExpandConstant('{%USERPROFILE}\brain'));
  if DocumentsRedirected then
    VaultPage.SubCaptionLabel.Caption := 'Documents is redirected (commonly OneDrive), so it is not the default. The vault stays under your user profile.'
  else
    VaultPage.SubCaptionLabel.Caption := VaultReason;
  PresetPage.CheckListBox.ItemCaption[0] := 'Cloud' + RecommendedSuffix('cloud') + ' - Claude does the summarising and the compile; nothing local to install';
  PresetPage.CheckListBox.ItemCaption[1] := 'Local' + RecommendedSuffix('local') + ' - your own model does the summarising; the nightly compile still runs on Claude';
  PresetPage.CheckListBox.ItemCaption[2] := 'Hybrid' + RecommendedSuffix('hybrid') + ' - your own model for summaries, Claude Code alongside it';
  if RecommendedPreset = 'local' then PresetIndex := 1
  else if RecommendedPreset = 'hybrid' then PresetIndex := 2
  else PresetIndex := 0;
  PresetPage.SelectedValueIndex := PresetIndex;
  if PythonOK then LoadModelChoices;
  UpdateSystemPage;
end;

procedure PrepareProbeTree;
var
  Root, Scripts: String;
begin
  // dontcopy extracts by file name into {tmp} and ignores DestDir, so the
  // layout kur.ps1 expects (scripts beside it) has to be built here.
  // Compiling proves none of this: it failed at run time on the first page.
  ExtractTemporaryFile('kur.ps1');
  ExtractTemporaryFile('donanim.py');
  ExtractTemporaryFile('model_oneri.py');
  ExtractTemporaryFile('kurulum_plani.py');
  Root := ExpandConstant('{tmp}\oom-probe');
  Scripts := Root + '\scripts';
  if not ForceDirectories(Scripts) then
    RaiseException('Could not create the probe directory.');
  if not FileCopy(ExpandConstant('{tmp}\kur.ps1'), Root + '\kur.ps1', False) then
    RaiseException('Could not stage kur.ps1.');
  if not FileCopy(ExpandConstant('{tmp}\donanim.py'), Scripts + '\donanim.py', False) then
    RaiseException('Could not stage donanim.py.');
  if not FileCopy(ExpandConstant('{tmp}\model_oneri.py'), Scripts + '\model_oneri.py', False) then
    RaiseException('Could not stage model_oneri.py.');
  if not FileCopy(ExpandConstant('{tmp}\kurulum_plani.py'), Scripts + '\kurulum_plani.py', False) then
    RaiseException('Could not stage kurulum_plani.py.');
end;

function RunDetection: Boolean;
var
  ScriptPath, Params: String;
  ResultCode: Integer;
begin
  Result := False;
  DetectionProgressPage.SetText('Checking this computer...', 'The existing kur.ps1 detection code is running.');
  DetectionProgressPage.SetProgress(0, 0);
  DetectionProgressPage.Show;
  try
    if not DetectionComplete then
    begin
      PrepareProbeTree;
    end;
    ScriptPath := ExpandConstant('{tmp}\oom-detect.ps1');
    DetectionIni := ExpandConstant('{tmp}\oom-detection.ini');
    if not SaveUTF8Text(ScriptPath, BuildDetectionScript) then
      RaiseException('Could not write the detection helper.');
    Params := '-NoProfile -ExecutionPolicy Bypass -File ' + Q(ScriptPath) +
      ' -Root ' + Q(ExpandConstant('{tmp}\oom-probe')) + ' -Out ' + Q(DetectionIni);
    if (not Exec(PSExe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
      RaiseException('System detection failed with exit code ' + IntToStr(ResultCode) + '.');
    DetectionComplete := True;
    ApplyDetection;
    Result := True;
  except
    MsgBox('System detection failed: ' + GetExceptionMessage + #13#10 +
      'Setup has not changed this computer.', mbError, MB_OK);
  finally
    DetectionProgressPage.Hide;
  end;
end;

function VerifyPythonInstaller(const InstallerPath: String): Boolean;
var
  Command, Params: String;
  ResultCode: Integer;
begin
  Command := '$s=Get-AuthenticodeSignature -LiteralPath ' + Q(InstallerPath) +
    '; if($s.Status -ne ''Valid'' -or $s.SignerCertificate.Subject -notlike ''*Python Software Foundation*''){exit 1}';
  Params := '-NoProfile -ExecutionPolicy Bypass -Command ' + Q(Command);
  Result := Exec(PSExe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function InstallPythonFromWeb: Boolean;
var
  URL, InstallerPath, Params: String;
  ResultCode: Integer;
begin
  Result := False;
  if IsWin64 then URL := PythonURL64 else URL := PythonURL32;
  InstallerPath := ExpandConstant('{tmp}\') + PythonInstallerName;
  try
    PythonProgressPage.SetText('Downloading Python 3.12...', 'Official python.org download; no elevation and no SmartScreen bypass.');
    DownloadTemporaryFile(URL, PythonInstallerName, '', nil);
    if not VerifyPythonInstaller(InstallerPath) then
      RaiseException('The Python installer signature was not valid for the Python Software Foundation.');
    PythonProgressPage.SetText('Installing Python for this user...', 'The official installer is running silently.');
    Params := '/quiet InstallAllUsers=0 PrependPath=1 Include_test=0 Include_launcher=0 Include_pip=1';
    if (not Exec(InstallerPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
      RaiseException('The Python installer returned exit code ' + IntToStr(ResultCode) + '.');
    Result := True;
  except
    MsgBox('Python installation failed: ' + GetExceptionMessage, mbError, MB_OK);
  end;
end;

function RemediatePython: Boolean;
var
  ResultCode: Integer;
  Installed: Boolean;
begin
  Result := True;
  if PythonPage.SelectedValueIndex = 2 then Exit;
  Installed := False;
  PythonProgressPage.SetProgress(0, 0);
  PythonProgressPage.Show;
  try
    if PythonPage.SelectedValueIndex = 0 then
    begin
      PythonProgressPage.SetText('Installing Python with winget...', 'Per-user Python 3.12; this may take a few minutes.');
      Installed := Exec(PSExe,
        '-NoProfile -ExecutionPolicy Bypass -Command "winget install --id Python.Python.3.12 -e --scope user --accept-source-agreements --accept-package-agreements"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
    end;
    if not Installed then Installed := InstallPythonFromWeb;
  finally
    PythonProgressPage.Hide;
  end;
  if not Installed then begin Result := False; Exit; end;
  PythonRemediated := True;
  DetectionComplete := True;
  ModelsLoaded := False;
  if not RunDetection then begin Result := False; Exit; end;
  if not PythonOK then
  begin
    MsgBox('Python finished installing, but Python 3.12+ is not visible yet. Restart Setup or choose "I will install it myself".', mbError, MB_OK);
    Result := False;
  end;
end;

function SelectedPreset: String;
begin
  case PresetPage.SelectedValueIndex of
    1: Result := 'local';
    2: Result := 'hybrid';
  else
    Result := 'cloud';
  end;
end;

function SelectedModelTag: String;
begin
  Result := IniValue('models', 'Tag' + IntToStr(ModelPage.SelectedValueIndex + 1), '');
end;

function BuildPlanJson: String;
var
  Preset, Vault, Backend, ModelTag, BackendEnv, PullModels: String;
  InstallRuntime, Mcp: Boolean;
begin
  Preset := SelectedPreset;
  Vault := VaultPage.Values[0];
  Mcp := IniBool('probe', 'McpPresent');
  if Preset = 'cloud' then
  begin
    Backend := 'claude';
    BackendEnv := '{"BEYIN_VAULT":' + JsonQuote(Vault) + ',"BEYIN_MODEL_BACKEND":"claude"}';
    PullModels := '[]';
    InstallRuntime := False;
  end
  else
  begin
    Backend := LocalModelBackend;
    ModelTag := SelectedModelTag;
    BackendEnv := '{"BEYIN_VAULT":' + JsonQuote(Vault) + ',"BEYIN_MODEL_BACKEND":' +
      JsonQuote(Backend) + ',"BEYIN_OLLAMA_MODEL_FAST":' + JsonQuote(ModelTag) + '}';
    PullModels := '[' + JsonQuote(ModelTag) + ']';
    InstallRuntime := IniBool('plan', 'InstallRuntime');
  end;
  Result := '{' +
    '"preset":' + JsonQuote(Preset) + ',' +
    '"vault":' + JsonQuote(Vault) + ',' +
    '"backend":' + JsonQuote(Backend) + ',' +
    '"backend_env":' + BackendEnv + ',' +
    '"mcp":' + JsonBool(Mcp) + ',' +
    '"skills":["beyin-doktor","beyin-ice-aktar"],' +
    '"force":false,' +
    '"install_runtime":' + JsonBool(InstallRuntime) + ',' +
    '"pull_models":' + PullModels + '}';
end;

function IsVaultPathValid: Boolean;
var
  Vault, AppPrefix: String;
begin
  Result := False;
  Vault := Trim(VaultPage.Values[0]);
  if Vault = '' then begin MsgBox('Choose a vault folder.', mbError, MB_OK); Exit; end;
  if FileExists(Vault) then begin MsgBox('The vault path points to a file. Choose a folder.', mbError, MB_OK); Exit; end;
  // WizardDirValue, not ExpandConstant('{app}'): with DisableDirPage the {app}
  // constant is not initialised while a custom page is still on screen, and
  // expanding it there aborts the wizard at run time. Compiling cannot see it.
  AppPrefix := AddBackslash(WizardDirValue);
  if (CompareText(Vault, WizardDirValue) = 0) or
     (Pos(Lowercase(AppPrefix), Lowercase(AddBackslash(Vault))) = 1) then
  begin
    MsgBox('The vault must not be inside the installed program folder.', mbError, MB_OK);
    Exit;
  end;
  Result := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = PythonPage.ID) and PythonOK) or
    ((PageID = ModelPage.ID) and (SelectedPreset = 'cloud'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpWelcome) and not DetectionComplete then
    Result := RunDetection
  else if CurPageID = PythonPage.ID then
    Result := RemediatePython
  else if CurPageID = VaultPage.ID then
    Result := IsVaultPathValid
  else if CurPageID = PresetPage.ID then
  begin
    if (SelectedPreset <> 'cloud') and not PythonOK then
    begin
      MsgBox('Local and hybrid setup need Python 3.12+ so model_oneri.py can choose a verified model. Choose Cloud or go Back and install Python.', mbError, MB_OK);
      Result := False;
    end
    else if (SelectedPreset <> 'cloud') and ((ModelCount = 0) or (LocalModelBackend = '')) then
    begin
      MsgBox('No verified local model recommendation is available. Choose Cloud or fix the unavailable hardware probe.', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = ModelPage.ID then
  begin
    if SelectedModelTag = '' then
    begin
      MsgBox('Choose the model recommended by model_oneri.py.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S, Preset, Backend, ModelTag, SizeText: String;
begin
  Preset := SelectedPreset;
  if Preset = 'cloud' then Backend := 'claude'
  else Backend := LocalModelBackend;
  S := 'Origin of Memory will:' + NewLine +
    Space + '- install its program files for this Windows user in ' + ExpandConstant('{app}') + NewLine +
    Space + '- create/configure the vault at ' + VaultPage.Values[0] + NewLine +
    Space + '- hand a validated ' + Preset + ' plan to kur.ps1 -Answers' + NewLine +
    Space + '- configure backend ' + Backend + NewLine +
    Space + '- install the two core skills: beyin-doktor and beyin-ice-aktar' + NewLine;
  if PythonRemediated then
    S := S + Space + '- use Python 3.12 installed for this user on the earlier Python page' + NewLine
  else if PythonOK then
    S := S + Space + '- keep the existing compatible Python installation' + NewLine;
  if Preset = 'cloud' then
    S := S + Space + '- use Claude; no local model or local runtime will be installed' + NewLine
  else
  begin
    ModelTag := SelectedModelTag;
    SizeText := IniValue('models', 'Size' + IntToStr(ModelPage.SelectedValueIndex + 1), '?');
    if OllamaPresent then
      S := S + Space + '- use the detected local runtime' + NewLine
    else
      S := S + Space + '- install the local runtime per-user through kur.ps1' + NewLine;
    S := S + Space + '- download model ' + ModelTag + ' (catalogue size ' + SizeText + ' GB)' + NewLine;
  end;
  if IniBool('probe', 'McpPresent') then
    S := S + Space + '- register Claude Desktop MCP access' + NewLine
  else
    S := S + Space + '- leave Claude Desktop MCP access unchanged' + NewLine;
  if not PythonOK then
    S := S + Space + '- leave Python for you to install; memory scripts require Python 3.12+' + NewLine;
  S := S + Space + '- add an Add/Remove Programs entry and a desktop shortcut by default' + NewLine + NewLine +
    'Nothing is installed system-wide and Setup never requests administrator rights.';
  Result := S;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  PlanPath := ExpandConstant('{tmp}\origin-of-memory-plan.json');
  if not SaveUTF8Text(PlanPath, BuildPlanJson) then
    Result := 'Could not write the installation plan JSON.'
  else
    Result := '';
end;

function ShouldCreateDesktopShortcut: Boolean;
begin
  Result := DesktopShortcutCheck.Checked;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Params, UninstallPlan: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Running kur.ps1 with the approved plan...';
  Params := '-NoProfile -ExecutionPolicy Bypass -File ' + Q(ExpandConstant('{app}\kur.ps1')) +
    ' -Answers ' + Q(PlanPath);
  if (not Exec(PSExe, Params, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
     (ResultCode <> 0) then
    RaiseException('kur.ps1 failed with exit code ' + IntToStr(ResultCode) + '. See the Setup log; installation was not completed.');
  UninstallPlan := '{"vault":' + JsonQuote(VaultPage.Values[0]) +
    ',"remove_scripts":true,"remove_hooks":true,"remove_skills":true}';
  if not SaveUTF8Text(ExpandConstant('{app}\uninstall-plan.json'), UninstallPlan) then
    RaiseException('Could not write the uninstall plan.');
  InstallSucceeded := True;
end;

procedure InitializeWizard;
begin
  DetectionIni := ExpandConstant('{tmp}\oom-detection.ini');
  DetectionProgressPage := CreateOutputProgressPage('System check', 'Inspecting this computer');
  PythonProgressPage := CreateOutputProgressPage('Python 3.12', 'Installing a per-user prerequisite');

  SystemPage := CreateOutputMsgMemoPage(wpWelcome, 'System check',
    'What is ready on this computer?',
    'Each item has one status and one consequence. No changes happen on this page.',
    'Detection will run after Welcome.');

  PythonPage := CreateInputOptionPage(SystemPage.ID, 'Python 3.12',
    'Python is missing or too old',
    'Choose one path. The recommended available choice is preselected.', True, False);
  PythonPage.Add('Install with winget (Recommended when available)');
  PythonPage.Add('Download from python.org and install for me');
  PythonPage.Add('I will install Python 3.12+ myself; continue with cloud setup');
  PythonPage.SelectedValueIndex := 0;

  VaultPage := CreateInputDirPage(PythonPage.ID, 'Vault location',
    'Where should your memory live?',
    'The default avoids cloud-synced Documents folders.', False, SetupMessage(msgNewFolderName));
  VaultPage.Add('');
  VaultPage.Values[0] := ExpandConstant('{%USERPROFILE}\brain');

  PresetPage := CreateInputOptionPage(VaultPage.ID, 'Setup preset',
    'How should Origin of Memory work?',
    'One recommendation is preselected from the existing system and hardware probe.', True, False);
  PresetPage.Add('Cloud - Claude handles capture and model work');
  PresetPage.Add('Local - local model work; Claude still powers capture/compile');
  PresetPage.Add('Hybrid - Claude capture plus local model work');
  PresetPage.SelectedValueIndex := 0;

  ModelPage := CreateInputOptionPage(PresetPage.ID, 'Local model',
    'Which verified model fits this computer?',
    'model_oneri.py ranks the choices; its top result is preselected.', True, True);

  DesktopShortcutCheck := TNewCheckBox.Create(WizardForm);
  DesktopShortcutCheck.Parent := WizardForm.FinishedPage;
  DesktopShortcutCheck.Left := ScaleX(24);
  DesktopShortcutCheck.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(18);
  DesktopShortcutCheck.Width := WizardForm.FinishedPage.ClientWidth - ScaleX(48);
  DesktopShortcutCheck.Caption := 'Create a desktop shortcut';
  DesktopShortcutCheck.Checked := True;

  HowItWorksCheck := TNewCheckBox.Create(WizardForm);
  HowItWorksCheck.Parent := WizardForm.FinishedPage;
  HowItWorksCheck.Left := DesktopShortcutCheck.Left;
  HowItWorksCheck.Top := DesktopShortcutCheck.Top + ScaleY(28);
  HowItWorksCheck.Width := DesktopShortcutCheck.Width;
  HowItWorksCheck.Caption := 'Show me how this works';
  HowItWorksCheck.Checked := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = PythonPage.ID then
  begin
    if WingetPresent then PythonPage.SelectedValueIndex := 0
    else PythonPage.SelectedValueIndex := 1;
  end;
end;

procedure DeinitializeSetup;
var
  ResultCode: Integer;
begin
  if not InstallSucceeded then Exit;
  if not DesktopShortcutCheck.Checked then
    DeleteFile(ExpandConstant('{autodesktop}\Origin of Memory Setup.lnk'));
  if HowItWorksCheck.Checked then
    Exec(ExpandConstant('{win}\notepad.exe'), Q(ExpandConstant('{app}\docs\install.md')),
      ExpandConstant('{app}\docs'), SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
