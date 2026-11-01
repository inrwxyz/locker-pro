namespace AppLocker.Core;

// Master Password er niyom: boro hater + choto hater letter, number, special character, kompokkhe MinLength.
public static class PasswordPolicy
{
    public const int MinLength = 10;

    public const string Rules =
        "Kompokkhe 10 character: ekta boro hater (A-Z), ekta choto hater (a-z), ekta number (0-9) " +
        "ebong ekta special character (! @ # $ % ^ & * ...) lagbe.";

    // null = password thik ache. Nahole ki bhul seta bole dey.
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Password kompokkhe {MinLength} character hote hobe.";

        bool upper = false, lower = false, digit = false, special = false;
        foreach (char c in password)
        {
            if (char.IsUpper(c)) upper = true;
            else if (char.IsLower(c)) lower = true;
            else if (char.IsDigit(c)) digit = true;
            else if (!char.IsWhiteSpace(c)) special = true;
        }

        if (!upper) return "Ekta boro hater letter (A-Z) lagbe.";
        if (!lower) return "Ekta choto hater letter (a-z) lagbe.";
        if (!digit) return "Ekta number (0-9) lagbe.";
        if (!special) return "Ekta special character (! @ # $ % ...) lagbe.";
        return null;
    }
}
