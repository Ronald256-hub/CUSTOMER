namespace Robo.Pos.Server.Security;

public static class PasswordPolicy
{
    public static PasswordPolicyResult Validate(
        string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Invalid(
                "password_required",
                "A new password is required.");
        }

        if (password.Length < 12)
        {
            return Invalid(
                "password_too_short",
                "The password must contain at least 12 characters.");
        }

        if (password.Length > 128)
        {
            return Invalid(
                "password_too_long",
                "The password cannot exceed 128 characters.");
        }

        if (!password.Any(char.IsUpper))
        {
            return Invalid(
                "password_uppercase_required",
                "The password must contain an uppercase letter.");
        }

        if (!password.Any(char.IsLower))
        {
            return Invalid(
                "password_lowercase_required",
                "The password must contain a lowercase letter.");
        }

        if (!password.Any(char.IsDigit))
        {
            return Invalid(
                "password_number_required",
                "The password must contain a number.");
        }

        if (!password.Any(character =>
                !char.IsLetterOrDigit(character)))
        {
            return Invalid(
                "password_symbol_required",
                "The password must contain a symbol.");
        }

        return new PasswordPolicyResult(
            IsValid: true,
            ErrorCode: null,
            Message: null);
    }

    private static PasswordPolicyResult Invalid(
        string errorCode,
        string message)
    {
        return new PasswordPolicyResult(
            IsValid: false,
            ErrorCode: errorCode,
            Message: message);
    }
}

public sealed record PasswordPolicyResult(
    bool IsValid,
    string? ErrorCode,
    string? Message);
