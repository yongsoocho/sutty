using sutty.Core.Sftp;

namespace sutty.UI.Helpers;

/// <summary>Phase labels never turn byte completion into a completed transfer.</summary>
internal static class TransferPhaseLabels
{
    public static string Active(SftpTransferPhase? phase, double fraction) => phase switch
    {
        SftpTransferPhase.Enumerating => Loc.T("파일 확인 중", "Listing files"),
        SftpTransferPhase.Preparing => Loc.T("준비 중", "Preparing"),
        SftpTransferPhase.Verifying => Loc.T("검증 중", "Verifying"),
        SftpTransferPhase.Promoting or SftpTransferPhase.Completed => Loc.T("최종 반영 중", "Finalizing"),
        SftpTransferPhase.Retrying => Loc.T("재시도 중", "Retrying"),
        _ when fraction >= 1 => Loc.T("데이터 전송됨 · 완료 확인 중", "Data transferred · awaiting completion"),
        _ => Loc.T("데이터 전송 중", "Transferring data"),
    };

    public static string Complete => Loc.T("최종 반영 완료", "Completed");
    public static string Bytes(double percentage) => Loc.T($"데이터 {percentage:0}%", $"Data {percentage:0}%");
}
