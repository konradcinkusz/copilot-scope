# 4. Wdrożenie zespołowe

**Sytuacja:** działa na Twojej maszynie i ktoś chce tego dla zespołu.
**Wynik:** wdrożenie, które da się obronić przed osobą robiącą przegląd
bezpieczeństwa i przed radą pracowniczą.

Przeczytaj to, zanim cokolwiek uruchomisz. W chwili, gdy telemetria drugiej
osoby trafia do tego samego kolektora, system zmienia kategorię.

To także inna instalacja. Natywny program `copilotscope` z samouczka 1 wiąże się wyłącznie ze
swoją maszyną. Wdrożenie współdzielone to stos Docker Compose z Postgresem, który ten sam
instalator stawia z opcją `--docker`:

```bash
curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh -s -- --docker
```

## Co się zmienia i dlaczego nie jest to opcjonalne

Kolektor trzymający sesje jednego programisty to narzędzie osobiste. Kolektor
trzymający sesje zespołu to **system techniczny zdolny do monitorowania
wydajności pracowników**. W Unii Europejskiej uruchamia to współdecydowanie rady
pracowniczej na podstawie samej zdolności, niezależnie od Twoich zamiarów.

Domyślne ustawienia dla jednej maszyny są więc ograniczone zakresem, a nie
rozluźnione:

| | Jedna maszyna | Współdzielone |
|---|---|---|
| Porty | `127.0.0.1` | publikowane świadomie |
| Klucz przyjmowania | brak — tryb otwarty | wymagany i podzielony na zakresy |
| Postgres | brak publikowanego portu, `trust` | hasło + `scram-sha-256` |
| Dashboard | otwarty | logowanie, dwie role |
| Tryb prywatności | wyłączony | włączony, zanim dotrą dane pierwszej innej osoby |

## Krok 1 — najpierw włącz tryb prywatności

Zanim skierujesz na niego edytor kogokolwiek innego, a nie po pierwszym pytaniu
o to.

```jsonc
// appsettings.json kolektora
{
  "CopilotScope": {
    "Privacy": {
      "Enabled": true,
      "Salt": "<długi losowy sekret, trzymany osobno od bazy danych>",
      "MinimumGroupSize": 5
    },
    "History": { "RetentionDays": 90 }
  }
}
```

Co egzekwuje:

- tożsamości pseudonimizowane **zanim cokolwiek je zapisze**;
- treść promptów i odpowiedzi odrzucana przy przyjęciu, niezależnie od
  konfiguracji klientów;
- żaden widok nie renderuje się dla mniej niż *k* programistów;
- każdy odczyt logowany, do tabeli, której nie dotyka zamiatanie retencji —
  zapis audytowy musi przeżyć sesje, które opisuje;
- przekazywanie surowego OTLP odrzucane, chyba że wprost stwierdzisz, że backend
  docelowy jest objęty tą samą umową.

Ustaw sól. Jeśli zapomnisz, zostanie wygenerowana ulotna, a kolektor ostrzeże
głośno, bo pseudonimy zmieniające się przy każdym restarcie przestają korelować
historię między wdrożeniami.

Zweryfikuj, co jest faktycznie egzekwowane, zamiast ufać plikowi:

```bash
curl http://localhost:4318/api/privacy
```

[`docs/PRIVACY.md`](../PRIVACY.md) zawiera mapę danych z art. 30 RODO,
zachowanie retencji i usuwania oraz wzór aneksu do porozumienia zakładowego.

## Krok 2 — opublikuj, z kluczem w tym samym ruchu

```bash
copilotscope up --bind 0.0.0.0 --api-key "$(openssl rand -hex 24)"
```

Zarówno instalator, jak i skrypt sterujący **odmawiają** `--bind` bez
`--api-key`, bo kolektor osiągalny w sieci bez klucza przyjmuje telemetrię,
wydaje transkrypty i pozwala kasować każdemu, kto dosięgnie portu. Kolektor
loguje też ostrzeżenie startowe, jeśli zastanie się opublikowany poza pętlą
zwrotną bez klucza, bo sam tego nie ustali — za Dockerem każde żądanie
przychodzi z bramy mostka.

Ustaw poświadczenia bazy w tej samej zmianie:

```bash
POSTGRES_PASSWORD=$(openssl rand -hex 16)
POSTGRES_HOST_AUTH_METHOD=scram-sha-256
```

## Krok 3 — podziel jeden klucz na zakresy

Klucz, który trzyma edytor każdego programisty, nie powinien być zarazem
kluczem czytającym transkrypty i kasującym historię.

```jsonc
"CopilotScope": { "Keys": {
  "Ingest": ["klucz-emitera"],     // tylko POST /v1/*
  "Read":   ["klucz-dashboardu"],  // /api/* + /metrics
  "Admin":  ["klucz-operatora"]    // usuwanie, seed, import — zawiera Read
} }
```

## Krok 4 — załóż hasło na dashboard

Transkrypty są wrażliwym ładunkiem: przy włączonym przechwytywaniu treści ten
tekst może zawierać kod źródłowy, poświadczenia wklejone przez kogoś do czatu i
dane klientów.

```jsonc
"CopilotScope": { "Dashboard": { "Auth": {
  "ViewerPassword": "…",   // oceny, tury, agregaty
  "AdminPassword":  "…"    // + transkrypty i usuwanie
} } }
```

## Krok 5 — terminuj TLS

Żaden plik compose tego nie robi. Te poświadczenia podróżują w nagłówku i w
ciasteczku, więc postaw reverse proxy przed czymkolwiek współdzielonym.

## Krok 6 — spraw, żeby produkował wyjście, a nie tylko dashboard

Dashboard, do którego trzeba wejść, zostaje porzucony.

```jsonc
"CopilotScope": { "Alerts": {
  "Enabled": true,
  "WebhookUrl": "https://hooks.example.com/services/…",
  "Format": "slack",
  "WindowDays": 7,
  "ScoreDropPoints": 5,
  "MinSessionsPerWindow": 10,
  "Digest": true
} }
```

Zwróć uwagę, co celowo *nie* odpala: spadek, któremu towarzyszył spadek
pewności, jest raportowany jako zmiana podstawy pomiaru, a nie regresja.
Kohorta, która przestała raportować sygnał, jest mierzona inaczej, a nie gorzej.

## Rzecz, którą trzeba powiedzieć zespołowi na głos

Ocena dotyczy **sesji**, a nie osoby. Nie ma widoku per programista, nie ma
takiej osi w żadnym filtrze kohort ani eksportu z taką kolumną — testy tego
pilnują, więc jest to egzekwowane, a nie obiecane.

Powiedz to przed pierwszym pytaniem o to, a nie po nim. Użytecznym pytaniem
jest „gdzie nasze narzędzia AI marnują ludziom czas'', a nie „kto jest
najlepszym programistą'', a narzędzie wdrożone z tym drugim pytaniem w
powietrzu spotka się z oporem niezależnie od tego, co robi jego kod.

## Dalsza lektura

- [`SECURITY.md`](../../SECURITY.md) — model zaufania, zakres po zakresie
- [`docs/PRIVACY.md`](../PRIVACY.md) — mapa danych, retencja, aneks do
  porozumienia
- [`GOVERNANCE.md`](../../GOVERNANCE.md) — kto to utrzymuje, co jest stabilne i
  co się stanie, jeśli opiekun przestanie
