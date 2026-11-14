-- データソース Main (SQLite) の表: ログインするユーザーと一時ファイル。NetSuite のレコードはここに置かない
-- 初期ユーザー: admin / admin
CREATE TABLE app_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_name TEXT NOT NULL UNIQUE,
    name TEXT,
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
