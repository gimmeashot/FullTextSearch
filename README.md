# FullTextSearch

Полнотекстовый поисковый движок на C# и .NET 10 без сторонних зависимостей.

Текущая версия: **0.2.0**. История изменений находится в [CHANGELOG.md](CHANGELOG.md).

## Возможности

- индексирование нескольких текстовых полей;
- Unicode-нормализация и поиск без учёта регистра;
- запросы с `AND`, `OR` и `NOT` (`OR` используется по умолчанию);
- поиск точных фраз: `"полнотекстовый поиск"`;
- префиксный поиск: `разраб*`;
- ранжирование BM25;
- фильтрация по полям и пагинация;
- HTML-безопасная подсветка совпадений;
- добавление, обновление и удаление документов;
- атомарное сохранение JSON-snapshot;
- потокобезопасное чтение и запись;
- HTTP API на ASP.NET Core.

## Запуск

```powershell
dotnet run --project src/FullTextSearch.Api
```

API по умолчанию будет доступен на адресе, который напечатает ASP.NET Core.

```powershell
Invoke-RestMethod -Method Put -Uri http://localhost:5000/documents/1 `
  -ContentType application/json `
  -Body '{"fields":{"title":"Полнотекстовый поиск","body":"Движок на C#"}}'

Invoke-RestMethod 'http://localhost:5000/search?q=%22полнотекстовый%20поиск%22'
Invoke-RestMethod 'http://localhost:5000/search?q=движ*%20NOT%20Java'
Invoke-RestMethod -Method Post http://localhost:5000/commit
```

Путь snapshot можно изменить через конфигурационный параметр `IndexPath`.

## Проверка

```powershell
dotnet build FullTextSearch.sln -m:1
dotnet run --project tests/FullTextSearch.Tests
```

## Текущие ограничения

- индекс целиком хранится в памяти;
- snapshot перезаписывается полностью;
- нет морфологического анализа и нечёткого поиска;
- язык запросов пока не поддерживает скобки и смешанный приоритет `AND`/`OR`;
- один процесс, без шардирования и репликации.

Следующий архитектурный этап — неизменяемые дисковые сегменты, позиции терминов и полноценное дерево запросов.
