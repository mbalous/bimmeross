namespace BmwCoder.Uds;

public static class NrcDescriptions
{
    public static string Describe(Nrc code) => code switch
    {
        Nrc.GeneralReject => "The ECU rejected the request without a more specific reason.",
        Nrc.ServiceNotSupported => "This ECU does not support the requested diagnostic service.",
        Nrc.SubFunctionNotSupported => "This ECU does not support the requested subfunction.",
        Nrc.IncorrectMessageLengthOrInvalidFormat => "The request length or format is invalid.",
        Nrc.ResponseTooLong => "The response exceeds the ECU's supported message size.",
        Nrc.BusyRepeatRequest => "The ECU is busy; retry later with a delay.",
        Nrc.ConditionsNotCorrect => "Vehicle or ECU conditions do not currently permit this request.",
        Nrc.RequestSequenceError => "The request arrived in an invalid diagnostic sequence.",
        Nrc.NoResponseFromSubnetComponent => "The gateway received no response from the downstream module.",
        Nrc.FailurePreventsExecutionOfRequestedAction => "An existing fault prevents this operation.",
        Nrc.RequestOutOfRange => "The identifier, value or requested range is unsupported or unavailable in the current session.",
        Nrc.SecurityAccessDenied => "The ECU requires authorization; read-only diagnostics will not attempt an unlock.",
        Nrc.InvalidKey => "The supplied security key was rejected.",
        Nrc.ExceedNumberOfAttempts => "The ECU's security-attempt limit has been reached.",
        Nrc.RequiredTimeDelayNotExpired => "The ECU's required security waiting period has not elapsed.",
        Nrc.UploadDownloadNotAccepted => "The ECU did not accept the transfer request.",
        Nrc.TransferDataSuspended => "The ECU suspended the data transfer.",
        Nrc.GeneralProgrammingFailure => "The ECU reported a programming failure.",
        Nrc.WrongBlockSequenceCounter => "The transfer block sequence number is incorrect.",
        Nrc.RequestCorrectlyReceivedResponsePending => "The request was accepted; the ECU is still preparing its response.",
        Nrc.SubFunctionNotSupportedInActiveSession => "This subfunction is unavailable in the active diagnostic session.",
        Nrc.ServiceNotSupportedInActiveSession => "This service is unavailable in the active diagnostic session.",
        _ => $"Unrecognized negative-response code 0x{(byte)code:X2}; raw response is preserved."
    };
}
