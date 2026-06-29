using Microsoft.AspNetCore.Identity;

namespace Obratka.WebApi.Auth;

// Русские тексты для ошибок ASP.NET Identity (по умолчанию они на английском и
// утекали в UI на регистрации/смене пароля). Переопределяем только те, что реально
// достижимы при нашей парольной политике (RequiredLength=8, RequireDigit,
// RequireLowercase) и флоу (смена пароля, смена email). Остальное наследуется.
public sealed class RussianIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => new()
        { Code = nameof(DefaultError), Description = "Произошла ошибка. Попробуйте ещё раз." };

    public override IdentityError PasswordTooShort(int length) => new()
        { Code = nameof(PasswordTooShort), Description = $"Пароль должен быть не короче {length} символов." };

    public override IdentityError PasswordRequiresDigit() => new()
        { Code = nameof(PasswordRequiresDigit), Description = "Пароль должен содержать хотя бы одну цифру." };

    public override IdentityError PasswordRequiresLower() => new()
        { Code = nameof(PasswordRequiresLower), Description = "Пароль должен содержать хотя бы одну строчную букву." };

    public override IdentityError PasswordRequiresUpper() => new()
        { Code = nameof(PasswordRequiresUpper), Description = "Пароль должен содержать хотя бы одну заглавную букву." };

    public override IdentityError PasswordRequiresNonAlphanumeric() => new()
        { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "Пароль должен содержать хотя бы один спецсимвол." };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new()
        { Code = nameof(PasswordRequiresUniqueChars), Description = $"Пароль должен содержать не менее {uniqueChars} различных символов." };

    public override IdentityError PasswordMismatch() => new()
        { Code = nameof(PasswordMismatch), Description = "Неверный текущий пароль." };

    public override IdentityError DuplicateEmail(string email) => new()
        { Code = nameof(DuplicateEmail), Description = $"Email «{email}» уже занят." };

    // UserName в нашем приложении = email, поэтому формулируем про email.
    public override IdentityError DuplicateUserName(string userName) => new()
        { Code = nameof(DuplicateUserName), Description = "Этот email уже зарегистрирован." };

    public override IdentityError InvalidEmail(string? email) => new()
        { Code = nameof(InvalidEmail), Description = $"Некорректный email «{email}»." };

    public override IdentityError InvalidUserName(string? userName) => new()
        { Code = nameof(InvalidUserName), Description = "Некорректный email." };
}
