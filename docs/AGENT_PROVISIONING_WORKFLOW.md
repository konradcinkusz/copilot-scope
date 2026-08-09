# Od telemetrii do Agenta — workflow provisioningu w AgentForge

> **Ten dokument jest po polsku**, zgodnie z konwencją `docs/ANALYSIS.md` — zapis
> rozumowania projektowego zostaje w języku, w którym powstał. Skrót dla czytelników
> anglojęzycznych: this document diagrams and explains the pipeline that turns
> consented session telemetry into a provisioned persona agent (`CopilotScope.AgentForge`),
> and draws a deliberate analogy to collecting warehouse-worker operational data to
> train a robot — same shape, same governance questions. Implementation reference:
> [`docs/AGENTFORGE.md`](AGENTFORGE.md).

## 1. Diagram

```mermaid
flowchart TB
    subgraph Src["Źródła telemetrii — emitery agnostyczne"]
        direction LR
        VSC["VS Code Copilot"]
        CLI["Copilot CLI"]
        CC["Claude Code / Cowork"]
        CUR["Cursor"]
    end

    subgraph Collect["Collector — dziś, bez tożsamości osobowej"]
        direction TB
        ING["OTLP ingest<br/><i>traces + metrics + logs</i>"]
        QE["Quality engine + insights<br/><i>10 algorytmów pomiaru, §8 ANALYSIS.md</i>"]
        PG[("Postgres<br/><i>sessions: id, score, jsonb snapshot</i>")]
        ING --> QE --> PG
    end

    subgraph Consent["Krok bramkujący — jawna zgoda, poza telemetrią"]
        direction TB
        COH["PersonaCohort<br/><i>PersonaId, ConsentGrantedBy, ConsentDate,<br/>ręcznie wypisane SessionIds</i>"]
        NOTE["Brak ścieżki inferencji<br/>z telemetrii do tożsamości"]
    end

    subgraph Forge["AgentForge — provisioning"]
        direction TB
        PB["PersonaProfileBuilder<br/><i>czyta TYLKO sesje z cohortu</i>"]
        PROF["Profil: avgQualityScore,<br/>commonTools, ograniczony zbiór<br/>exemplarów transkryptu"]
        PROMPT["System prompt<br/><i>grounding w kontekście —<br/>bez fine-tuningu wag</i>"]
        MAF["Microsoft Agent Framework<br/>+ Azure AI Foundry"]
        AGENT(["Sprowidencjonowany agent<br/>trzymany w pamięci procesu"])
        PB --> PROF --> PROMPT --> MAF --> AGENT
    end

    subgraph Serve["Serwowanie"]
        CHAT["POST /personas/{id}/chat<br/><i>odpowiedź zawsze niesie<br/>\"simulated\": true</i>"]
    end

    subgraph Revoke["Wycofanie zgody"]
        direction LR
        DEL["DELETE /personas/{id}<br/><i>natychmiast czyści agenta<br/>z pamięci → 409 do reprowizji</i>"]
        CFGRM["Usunięcie PersonaCohort<br/>z konfiguracji + restart<br/><i>trwałe — brak danych do odczytu</i>"]
    end

    Src -- "OTLP/HTTP :4318" --> ING
    PG -- "GET /api/sessions/{id}<br/>tylko id z cohortu" --> PB
    COH -.-> PB
    NOTE -.-> COH
    AGENT --> CHAT
    CHAT -. "revoke" .-> DEL
    DEL -. "trwałe wycofanie" .-> CFGRM

    classDef ext fill:#F1EFE8,stroke:#5F5E5A,stroke-width:1px,color:#2C2C2A
    classDef core fill:#EEEDFE,stroke:#3C3489,stroke-width:1px,color:#26215C
    classDef gate fill:#FAECE7,stroke:#993C1D,stroke-width:1px,color:#4A1B0C
    classDef forge fill:#FAEEDA,stroke:#854F0B,stroke-width:1px,color:#412402
    classDef revoke fill:#E9F3EC,stroke:#2F6B4F,stroke-width:1px,color:#173A2A

    class VSC,CLI,CC,CUR ext
    class ING,QE,PG core
    class COH,NOTE gate
    class PB,PROF,PROMPT,MAF,AGENT forge
    class CHAT core
    class DEL,CFGRM revoke
```

## 2. Opis procesu

**Krok 1 — Telemetria, bez tożsamości.** Cztery agnostyczne emitery (VS Code, Copilot
CLI, Claude Code/Cowork, Cursor) wysyłają OTLP na jeden endpoint. Collector agreguje to
w sesje w Postgresie — kluczem jest `gen_ai.conversation.id`, **nie** identyfikator
osoby. Ten etap istnieje niezależnie od AgentForge i działa dokładnie tak samo, czy
ktoś kiedykolwiek uruchomi provisioning agenta, czy nie.

**Krok 2 — Bramka zgody, poza systemem telemetrii.** AgentForge nie ma żadnego
mechanizmu, który sam wywnioskowałby "które sesje należą do której osoby" z danych.
Jedyne wejście to `PersonaCohort` — rekord konfiguracyjny wpisywany ręcznie, wiążący
`PersonaId` z konkretną, wyliczoną listą `SessionIds`, oraz polami `ConsentGrantedBy` i
`ConsentDate`. Bez wpisu w konfiguracji nie ma danych dla danej persony — to nie jest
domyślne "wyłączone", to brak ścieżki kodu, która mogłaby to zrobić automatycznie.

**Krok 3 — Budowa profilu.** `PersonaProfileBuilder` czyta *wyłącznie* sesje wymienione
w cohorcie (przez istniejące REST API Collectora, nie bezpośrednio z bazy) i składa z
nich profil: średni quality score, najczęściej używane narzędzia, oraz **ograniczony**
zestaw exemplarów transkryptu (repo już z założenia przechowuje transkrypty obcięte do
ostatnich ~100 wpisów po 4000 znaków — provisioning dziedziczy ten limit, nie rozszerza
go).

**Krok 4 — Grounding, nie fine-tuning.** Profil trafia do system promptu. To kluczowa
decyzja architektoniczna: agent nie jest douczany na wagach na podstawie sesji — jego
"styl" pochodzi z kontekstu w prompt, nie z gradientu. Oznacza to też, że wycofanie
zgody jest odwracalne w praktyce (patrz krok 6) — nic nie zostało wypieczone w wagi
modelu.

**Krok 5 — Provisioning.** System prompt + wybrany model hostowany w Azure AI Foundry
łączą się przez Microsoft Agent Framework. Wynik jest cache'owany w pamięci procesu
(`POST /personas/{id}/provision`) — kolejne żądania czatu nie odbudowują profilu za
każdym razem.

**Krok 6 — Serwowanie z etykietą.** Każda odpowiedź z `POST /personas/{id}/chat` niesie
pole `"simulated": true` zapisane na sztywno w DTO odpowiedzi — nie w konfiguracji, więc
nie da się go wyłączyć. Agent nigdy nie ma prawa przedstawiać się jako dana osoba.

**Krok 7 — Wycofanie, dwupoziomowe.** `DELETE /personas/{id}` czyści aktywnego agenta z
pamięci natychmiast — kolejne żądania czatu dostają `409`, dopóki ktoś nie
sprowizjonuje go ponownie. To poziom "operacyjny". Poziom "trwały" to usunięcie wpisu
`PersonaCohort` z konfiguracji i restart/redeploy — od tego momentu
`PersonaProfileBuilder` nie ma żadnych `SessionIds` do odczytania i agenta nie da się
odtworzyć w ogóle.

## 3. Analogia: telemetria deweloperska ↔ dane operacyjne pracownika magazynu

To nie jest luźne porównanie — kształt problemu jest identyczny, tylko domena inna.
W obu przypadkach zbieramy **dane behawioralne o pracy człowieka**, żeby zbudować coś,
co potem działa (częściowo) w jego imieniu.

| | Telemetria dewelopera → AgentForge | Dane pracownika magazynu → robot/cobot |
|---|---|---|
| **Co się zbiera** | Tury czatu, wywołania narzędzi, akceptacje/odrzucenia edycji, TTFT, błędy | Ścieżki ruchu, czasy cykli pick/pack, punkty korekt, interakcje z regałem/AMR |
| **Domyślna granulacja** | Metadane; treść promptów tylko z jawnie włączonym `captureContent` | Zdarzenia zadaniowe (skan, odłożenie, błąd); pełny feed wideo/RFID zwykle osobno i mocniej ograniczony |
| **Tożsamość** | Brak `user.id` w Collectorze — sesja to nie osoba | Identyfikator odznaki/terminala zwykle *jest* w systemie WMS — to większe ryzyko niż stan wyjściowy CopilotScope |
| **Bramka do "modelu jednej osoby"** | `PersonaCohort` — ręczny, jawny, z polami zgody | Program pilotażowy z podpisaną zgodą, konkretne zmiany/stanowiska wskazane do nagrania |
| **Co robi model z danymi** | Grounding w kontekście (in-context), nie fine-tuning wag | Najczęściej *imitation learning* / behavior cloning na trajektoriach — tu dane faktycznie trafiają w wagi polityki ruchu |
| **Odwracalność wycofania** | Wysoka — nic nie było w wagach, usunięcie cohortu = zero śladu | Niska, jeśli trajektorie już weszły do treningu — wymaga retrainu lub filtrowania datasetu, nie samego "delete" |
| **Etykieta / ujawnienie** | `"simulated": true` na każdej odpowiedzi, na stałe | Analogiczny wymóg: robot/cobot nie powinien być prezentowany jako "działa jak pracownik X"; polityka ruchu to agregat, nie kopia |
| **Zakaz nadużycia wprost w dokumentacji** | "Not for performance reviews" — CopilotScope świadomie nie ma widoku per-developer | Ten sam zakaz musi obowiązywać tu explicité: dane ruchu nie mogą stać się rankingiem "kto jest wolniejszy" |
| **Ryzyko Goodharta** | Jeśli deweloperzy wiedzą, że akceptacje trafiają do metryki, optymalizują pod metrykę, nie pod jakość | Jeśli pracownicy wiedzą, że ich ruchy trenują robota lub oceniają wydajność, zmieniają zachowanie pod obserwacją (efekt Hawthorne'a) — dataset przestaje być reprezentatywny |

Największa różnica techniczna to właśnie krok 4 w diagramie: AgentForge świadomie
wybrał grounding zamiast fine-tuningu, bo odwracalność zgody była wymaganiem od
początku. Program treningu robota z danych pracowników **nie ma tej opcji za darmo** —
jeśli trajektorie trafią do wag polityki (imitation learning), wycofanie zgody wymaga
albo utrzymywania danych źródłowych i retrainu bez nich, albo technik "machine
unlearning", które wciąż są przedmiotem badań, a nie gotową funkcją `DELETE`. To
wniosek wart wpisania w każdy przyszły projekt tego typu na wejściu, nie po fakcie:
**jeśli zgoda ma być odwracalna, architektura modelu musi to umożliwiać od pierwszego
dnia — nie da się tego dokleić później.**

## Powiązane dokumenty

- [`docs/AGENTFORGE.md`](AGENTFORGE.md) — implementacja, `PersonaCohort`, przykłady curl.
- [`docs/ANALYSIS.md`](ANALYSIS.md) §8 — dziesięć algorytmów pomiaru jakości sesji, na
  bazie których dane trafiają do profilu persony.
- [`research/RESEARCH_PROPOSALS.md`](../research/RESEARCH_PROPOSALS.md) — dziesięć
  propozycji prac badawczych, jedna na algorytm z §8.
