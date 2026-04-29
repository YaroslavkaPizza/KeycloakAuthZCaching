namespace Shifts_Tools.Services.Plumbing.Authorization
{
    /// <summary>
    /// Режим проверки разрешений
    /// </summary>
    public enum PermissionCheckMode
    {
        /// <summary>
        /// Требуется хотя бы одно разрешение (OR)
        /// </summary>
        Affirmative,

        /// <summary>
        /// Требуются все разрешения (AND)
        /// </summary>
        Unanimous
    }
}
