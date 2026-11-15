-- データソース Main (SQLite) の表: ログインするユーザーと一時ファイル。NetSuite のレコードはここに置かない
-- 初期ユーザー: admin / admin
-- email = NetSuite のアカウントでログインするときに突き合わせるメール (AppUser の LoginAccountContractField の ExternalLoginName)。
-- ichiro.sato はモックの従業員 (mock-data.json の employee) のメール。パスワード (hash / salt) が空なので NetSuite のログイン専用
CREATE TABLE app_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_name TEXT NOT NULL UNIQUE,
    name TEXT,
    email TEXT,
    hash TEXT NOT NULL,
    salt TEXT NOT NULL,
    session_stamp TEXT
);

CREATE TABLE temporary_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guid TEXT NOT NULL,
    created_date_time TEXT NULL
);

INSERT INTO app_users (id, user_name, name, hash, salt) VALUES
(1, 'admin', '管理者', 'pUNVPHEWYngAim7aUFjf6kWtIROxrpzJPprcipyPujw=', 'CPYn8K+ecURogSHP5KTZ04lEbiAjZoq9wvwk5aIAQQg=');

INSERT INTO app_users (id, user_name, name, email, hash, salt) VALUES
(2, 'ichiro.sato', '佐藤 一郎', 'ichiro.sato@example.com', '', '');
