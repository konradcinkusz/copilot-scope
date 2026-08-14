# CopilotScope — przegląd kompletności, UX i sensu biznesowego

> Przegląd z 2026-08-14, working tree `master` @ `1aa8c0a`. Dokument uzupełnia
> [`ARCHITECTURE_REVIEW.md`](ARCHITECTURE_REVIEW.md) (sierpień 2026) o trzy osie, których
> tamten przegląd nie obejmował: pełną zgodność z konstytucją
> `architecture-standards/docs/architecture/00-REFERENCE-ARCHITECTURE.md` (P1–P15 +
> przewodniki operacyjne), audyt UI/UX na **żywej aplikacji** (uruchomionej i zasilonej
> danymi w trakcie przeglądu) oraz analizę rynkową (~25 narzędzi konkurencyjnych,
> zweryfikowanych źródłami). Zawiera też erratę do ARCHITECTURE_REVIEW.md i listę wzorców
> wartych ekstrakcji do `architecture-standards`.

---

## 1. Werdykt skrócony

**CopilotScope to projekt o nieprzeciętnie dobrym rdzeniu algorytmiczno-domenowym
(P3/P10/P11 — wzorcowe, cytowane w konstytucji jako przykłady źródłowe), otoczonym
niedokończoną powłoką operacyjną.** Od czasu ARCHITECTURE_REVIEW.md **żadna** z 9 akcji
naprawczych nie została wdrożona (jedyny commit po `7d135c2` to sam dokument przeglądu).

| Pytanie | Odpowiedź |
|---|---|
| Zgodność z konstytucją (15 zasad) | **3 ZGODNE · 8 CZĘŚCIOWO · 4 NIEZGODNE** |
| Czy da się dziś wdrożyć z obrazów GHCR (główna reklamowana ścieżka)? | **Nie** — wszystkie 4 obrazy są martwe na starcie (`aspnet:10.0` vs `net8.0`) |
| Czy da się dziś wdrożyć lokalnie ze źródeł? | **Tak, i działa bez zarzutu** (zweryfikowane na żywo: SDK 8, dwa `dotnet run`, zero błędów) |
| Czy da się wdrożyć na współdzielonym hoście / w sieci? | **Nie bez poprawek bezpieczeństwa** (otwarte read/DELETE API, transkrypty bez auth, znany klucz `dev-secret-123`) |
| Czy nisza rynkowa istnieje? | **Tak — i jest realnie niezajęta**, ale wąska i krucha (szczegóły §5) |
| Czy UI jest używalne? | **Rdzeń tak, domyślne ustawienia nie** — najlepszy widok (Basic) jest ukryty za domyślnym firehose'em (Advanced) |

---

## 2. Zgodność z konstytucją — tabela zbiorcza

Pełne dowody (plik:linia) w podsekcjach niżej; tu sam wynik.

| Zasada | Status | Jednym zdaniem |
|---|---|---|
| P1 AppHost jako composition root | CZĘŚCIOWO | Port 4318 przypięty wzorcowo; ale zero `WithHttpHealthCheck` i skew `Aspire.AppHost.Sdk` 9.3.0 vs `Aspire.Hosting.*` 13.4.6 (`AppHost.csproj:3,15-16`) |
| P2 (+P2a) Shared kernel | **NIEZGODNY** | Brak ServiceDefaults/Contracts; agenci biorą `ProjectReference` na cały Collector; `CollectorClient` w trzech kopiach |
| P3 Bounded context / DB per serwis | **ZGODNY** | Npgsql tylko w Collectorze; wszystkie odczyty po HTTP; monolit kolektora sankcjonowany wprost przez konstytucję |
| P4 Persystencja migrowana | CZĘŚCIOWO | `CREATE TABLE IF NOT EXISTS` (`SessionRepository.cs:16-32`) zamiast migracji; odstępstwo obronialne przy snapshot-jsonb, ale nigdzie nie zapisane jako decyzja |
| P5 Konfiguracja/sekrety | CZĘŚCIOWO | Model konfiguracji wzorowy, ale: zero skanera sekretów, `dev-secret-123` w 5 plikach, otwarte read/DELETE API przy kluczowanym ingest |
| P6 Kontener per serwis | **NIEZGODNY** | 4× `sdk:10.0`/`aspnet:10.0` przy `net8.0` = obrazy nie startują; komentarz w Dockerfile twierdzi coś przeciwnego linijkę wyżej; brak warstwy restore/`USER`/`LABEL`; HEALTHCHECK na `wget`, którego w obrazie zapewne nie ma |
| P7 Fly.io / topologia kosztowa | **NIEZGODNY** | Zero `fly.toml`; jedyne IaC (`infra/main.bicep`) wdraża 1 z 4 serwisów, bez Postgresa — kolektor w ACA traci dane przy restarcie |
| P8 Opcjonalne zależności degradują | CZĘŚCIOWO | Rdzeń wzorowy (in-memory fallback, forwarder z drop-oldest); ale brak konfiguracji Azure w agentach = goły HTTP 500, a README:233 opisuje mechanizm warunkowej rejestracji cloud-analyzerów, **który nie istnieje w kodzie** |
| P9 Program.cs jako manifest | CZĘŚCIOWO | Zero klas `*Extensions` w całym repo; check klucza API skopiowany 5× w 3 serwisach; logika domenowa w ciałach endpointów |
| P10 Interface + DI, nie dziedziczenie | **ZGODNY** | `IInsightAnalyzer` + 5 rejestracji, fail-soft pipeline, zero klas abstrakcyjnych — przykład źródłowy konstytucji, potwierdzony testem (`CollectorTests.cs:612`) |
| P11 Anti-corruption na brzegu | **ZGODNY** | `Sem.cs`/`ClaudeCode.cs` — trzy dialekty vendorów w jednym pliku mapowania; poza `Domain`/`Otlp` przestrzenie vendorowe nie występują (grep czysty) |
| P12 Tag-driven CI/CD | CZĘŚCIOWO | Build-half dobra (tagi, matrix, fail-fast:false z uzasadnieniem); brak change detection, cache warstw, **jakiegokolwiek etapu deploy** i smoke-testu — pipeline publikuje martwe obrazy i nic tego nie łapie |
| P13 Testy na warstwie z logiką | CZĘŚCIOWO | 90 solidnych testów jednostkowych (próbka 10 przeszła pełny bar jakości); ale Dashboard: **0 testów**, warstwa HTTP kolektora (auth/gzip/seed): 0, integracja z realnym Postgresem: 0, E2E: 0 |
| P14 Dokumentacja z uzasadnieniami | CZĘŚCIOWO | Strona „reasoning" ponadprzeciętna (STRATEGY.md, How-not-to-use); ale komentarze Dockerfile **aktywnie kłamią** o wersjach, README:82 podaje Aspire 9.3, CHANGELOG deklaruje 2 obrazy zamiast 4 |
| P15 Observability jako decyzja buildu | **NIEZGODNY** | Zero pakietów OTel, zero `ActivitySource`, Dashboard bez żadnego endpointu health — produkt observability sam jest nieobserwowalny (znane odstępstwo §3a konstytucji, bez ruchu od przeglądu) |

### 2a. Przewodniki operacyjne (baseline / testy / security) — oceny 3/5 · 3/5 · 3/5

**Baseline (REPO-BASELINE):** komplet plików higieny z realną treścią (SECURITY.md z
uczciwym modelem zagrożeń, CONTRIBUTING z pitfallem `NETSDK1147` op