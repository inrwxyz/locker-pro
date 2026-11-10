namespace AppLocker.Core;

// Master Password er niyom: boro hater + choto hater letter, number, special character, kompokkhe MinLength.
public static class PasswordPolicy
{
    public const int MinLength = 10;

    public const string Rules =
        "At least 10 characters: 1 uppercase (A-Z), 1 lowercase (a-z), 1 digit (0-9) " +
        "and 1 special character (! @ # $ % ^ & * ...).";

    // null = valid password. Otherwise returns the validation error message.
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";

        bool upper = false, lower = false, digit = false, special = false;
        foreach (char c in password)
        {
            if (char.IsUpper(c)) upper = true;
            else if (char.IsLower(c)) lower = true;
            else if (char.IsDigit(c)) digit = true;
            else if (!char.IsWhiteSpace(c)) special = true;
        }

        if (!upper) return "Include at least one uppercase letter (A-Z).";
        if (!lower) return "Include at least one lowercase letter (a-z).";
        if (!digit) return "Include at least one number (0-9).";
        if (!special) return "Include at least one special character (! @ # $ % ...).";
        return null;
    }
}
