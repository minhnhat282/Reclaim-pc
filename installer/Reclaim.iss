; Reclaim - script tạo file cài đặt (Inno Setup 6)
; File cài đặt này chỉ "giải nén tạm rồi chạy lệnh cài đặt của chính Reclaim"
; (Reclaim.Service.exe install). Việc gỡ cài đặt do Reclaim tự lo qua Apps & features (có hỏi mật khẩu).

#define AppName "Reclaim"
#define AppVersion "1.0.0"
#define SourceDir "..\build\Release"

[Setup]
AppId={{B7F0E6A1-5C3D-4E8A-9A41-2D6F8C1E7A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Reclaim
CreateAppDir=no
Uninstallable=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
MinVersion=10.0
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=Output
OutputBaseFilename=ReclaimSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Reclaim.Desktop\Assets\Reclaim.ico

[Messages]
WelcomeLabel1=Cài đặt Reclaim
WelcomeLabel2=Reclaim bảo vệ DNS của máy này bằng NextDNS.%n%nTrình cài đặt sẽ cài dịch vụ Reclaim (tự chạy cùng Windows) và thêm mục vào Start Menu.

[Files]
Source: "{#SourceDir}\*"; DestDir: "{tmp}\pkg"; Excludes: "*.pdb,*.xml"; Flags: recursesubdirs createallsubdirs ignoreversion deleteafterinstall

[Run]
Filename: "{commonpf64}\Reclaim\Reclaim.exe"; Description: "Mở Reclaim"; Flags: postinstall nowait skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := True;
  // .NET Framework 4.8 có Release >= 528040
  if not RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
     or (Release < 528040) then
  begin
    MsgBox('Reclaim cần .NET Framework 4.8. Hãy cập nhật Windows hoặc cài .NET Framework 4.8 rồi chạy lại trình cài đặt.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if (not Exec(ExpandConstant('{tmp}\pkg\Reclaim.Service.exe'), 'install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode))
       or (ResultCode <> 0) then
    begin
      MsgBox('Không cài đặt được Reclaim (mã lỗi ' + IntToStr(ResultCode) + ').' + #13#10 +
             'Nếu Reclaim đã được cài và đang bật tự bảo vệ: mở Reclaim, mở khóa, chọn "Thêm... > Tạm gỡ bảo vệ 5 phút" rồi chạy lại trình cài đặt.',
             mbError, MB_OK);
      Abort;
    end;
  end;
end;
