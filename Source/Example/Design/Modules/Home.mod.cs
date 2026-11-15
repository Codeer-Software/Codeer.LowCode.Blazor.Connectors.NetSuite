//ログイン中のユーザーの表示名を出す (ユーザーモジュール AppUser / Employee のどちらにも 表示名 のフィールドがある)
void DetailLayoutDesign_OnAfterInitialization()
{
    CurrentUserName.Text = "ログイン中: " + CurrentUser.表示名.Value;
}
