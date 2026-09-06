# Foundry Chat

ASP.NET Core MVC で構築した、Microsoft Foundry Agent / Foundry IQ 向けのドキュメント検索チャットです。回答本文に加えて、Foundry が返した URI・ファイル引用、生成画像、生成ファイルを表示します。

## 必要な環境

- .NET SDK 10
- Microsoft Entra ID のアプリ登録
- Foundry Agent Service 2.x で作成済みの Agent
- ローカル実行時は `az login` 済みの Azure CLI

このアプリは旧 Classic Agents の Agent ID ではなく、現行 Agent Service の **Agent 名**（必要に応じて Version）を使用します。

## ローカル構成

Entra ID アプリ登録で次を構成します。

1. **Authentication** の Web リダイレクト URI に `https://localhost:7186/signin-oidc` を追加します。
2. **Certificates & secrets** で Client Secret を作成します。
3. **API permissions** で Azure Service Management の delegated permission `user_impersonation` を追加し、必要に応じて管理者の同意を付与します。
4. Implicit grant の ID token は有効化不要です。このアプリは Authorization Code + PKCE を使用します。

作成した値を User Secrets に設定します。

```powershell
dotnet user-secrets set "AzureAd:TenantId" "<tenant-id>" --project .\src\image-search-demo.csproj
dotnet user-secrets set "AzureAd:ClientId" "<app-registration-client-id>" --project .\src\image-search-demo.csproj
dotnet user-secrets set "AzureAd:ClientSecret" "<client-secret-value>" --project .\src\image-search-demo.csproj
az login
dotnet run --project .\src\image-search-demo.csproj --launch-profile https
```

ブラウザーで `https://localhost:7186` を開くと、サインインユーザーが Azure RBAC で参照できる Foundry リソースと Project が自動取得されます。Project を選ぶと Agent と Version が読み込まれます。

`Foundry:AllowedProjects` は不要です。Resource Graph から取得した Project Endpoint をユーザーに紐づく期限付き暗号化トークンとして保持するため、ブラウザーから任意 URL を指定することはできません。

## Azure PaaS への配置

App Service や Container Apps などの HTTPS 対応 PaaS を利用できます。

1. PaaS に Managed Identity を有効化します。
2. その Identity に、対象 Foundry Project の **Foundry User** ロールを付与します。Agent 一覧取得と実行に使用するため、リソース全体には付与せず、必要最小限の Project スコープに限定してください。
3. Blob 引用を中継する Storage Container に、その Identity の **Storage Blob Data Reader** ロールを付与します。ローカル実行時は `az login` したユーザーにも同じデータロールが必要です。
4. `AzureAd__TenantId`、`AzureAd__ClientId`、`AzureAd__ClientSecret` をアプリ設定に登録します。本番では可能であれば Client Secret の代わりに証明書認証を使用してください。
5. Entra ID アプリ登録に本番 URL の `/signin-oidc` を Web リダイレクト URI として追加します。
6. Container Apps などの TLS 終端 Proxy 配下では `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` を設定します。
7. アプリを Release 構成で発行します。

```powershell
dotnet publish .\src\image-search-demo.csproj -c Release -o .\publish
```

システム割り当て Managed Identity では追加の資格情報設定は不要です。ユーザー割り当て Managed Identity を使う場合は、その Client ID を `AZURE_CLIENT_ID` に設定します。

Foundry IQ が Azure AI Search を利用するための Project Identity / Search 側 RBAC は、作成済み Agent の既存構成を維持してください。

## データの保持

- 接続設定と会話トークン: ブラウザーの `sessionStorage`（タブを閉じると削除）
- 表示用チャット履歴: ブラウザーの `localStorage`
- Foundry Conversation と生成ファイルの参照: Data Protection による期限付き暗号化トークン
- 選択した Foundry Project と Blob 引用: ユーザーに紐づく期限付き暗号化トークン
- 生成画像: 応答内の Data URL（最大 8 MB）

接続先を変更すると新しい Conversation を開始しますが、表示済みの履歴は残ります。複数インスタンスで運用する場合は Data Protection のキーリングと Microsoft Identity Web の token cache を全インスタンスで共有してください。Azure App Service では既定の `%HOME%` キー保存を維持し、コンテナー系 PaaS では Blob Storage や Redis などの共有キー保存を明示的に構成します。

## セキュリティ

- 全画面/API は Microsoft Entra ID 認証必須です。
- Foundry Resource/Project の列挙は、サインインユーザーの delegated ARM token と Azure RBAC を使用します。
- Foundry への認証は `DefaultAzureCredential` を使用し、ブラウザーに Azure 資格情報を渡しません。
- Agent の列挙・実行と Blob の取得は、ローカルでは Azure CLI ユーザー、PaaS では Managed Identity の権限を使用します。
- 変更 API は Anti-forgery Token を検証します。
- Blob 引用は元 URL（SAS/query を除外）を表示し、実アクセスは認証済み Web アプリの中継 Endpoint を使用します。
- 生成ファイルはユーザーに紐づく短期ハンドル経由で配信し、Container/File ID を直接公開しません。
- SVG などのアクティブ形式はインライン表示せず、ダウンロードとして配信します。
