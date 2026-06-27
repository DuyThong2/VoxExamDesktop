# Question model alignment: Java structure as source of truth

## Context

`MockExamDataFactory.cs` is the only source of exam/question data right now (no real fetch from
Java yet — see `ai-examiner-plan.md`, out of scope). It's meant to stand in for real data that
will eventually come from Java, so its **structure** should mirror Java's real `Question` /
`QuestionEvaluationGuide` entities — Java is the source of truth for shape. The actual
**values** in the mock stay WPF's call (it's test data), but they need to be values Java's real
enums would actually produce, and that Python can actually parse.

Verified directly against `vox/src/main/java/com/sep/vox/domain/model/question/Question.java`
and `QuestionType.java` — current mock data deviates in both places the user suspected.

## Comparison

| Java `Question.java` field | Java type | WPF `Question.cs` today | Gap |
|---|---|---|---|
| `id` | UUID | `Id` (Guid) | OK |
| `code` | String | `Code` | OK |
| `instructionText` | String | `InstructionText` | OK |
| `questionText` | String | `QuestionText` | OK |
| `promptText` | String | `PromptText` | OK structurally (separate semantics issue already flagged elsewhere: mock values read like internal labels, not literal spoken text — not this doc's concern) |
| `preparationText` | String | `PreparationText` | OK |
| `preparationTimeSeconds` / `minResponseSeconds` / `maxResponseSeconds` | int | same names | OK |
| **`type`** (`QuestionType` enum: `READ_ALOUD`, `SHORT_ANSWER`, `LONG_ANSWER`, `OPINION`, `DESCRIPTION`) | enum, **on `Question` itself** | **missing from `Question.cs` entirely** — instead a loose `string QuestionType` lives on the wrapper `ExamPaperQuestion.cs`, with values like `"personal_introduction"`, `"long_turn"`, `"opinion_discussion"`, `"policy_discussion"`, `"problem_solving"`, `"applied_reasoning"` that **match none of Java's 5 real enum constants** | **real bug** — fix below |
| *(no equivalent — `Question.java` has no difficulty field, and `vox`'s question package has no `DifficultyLevel.java` at all)* | — | `DifficultyLevel` string on `ExamPaperQuestion.cs`, values `"easy"`/`"medium"` | not a Java-alignment issue (Java has nothing to align to) — this field exists only because Python's `QuestionContext.difficulty_level` wants it; keep it, just don't think of it as "matching Java" |
| `scope`, `visibility`, `sourceQuestionId`, `reviewerId`, `locked`, `status`, `createdAt`/`updatedAt`/`createdBy`/`updatedBy` | various | not modeled | content-management/workflow concerns, irrelevant to taking an already-published exam — intentionally not in scope, don't add |

`QuestionEvaluationGuide` (Java) vs WPF's `QuestionEvaluationGuide.cs`: **already a perfect
field-for-field match** (`id`, `questionId`, `expectedContent`, `keyPoints`,
`acceptableResponses`, `offTopicExamples`, `scoringHints`, `commonMistakes`) — nothing to fix
there.

## Fix

**1. `Models/Question.cs`** — add a `Type` field, structurally where Java has it (on the question
itself, not the wrapper):

```csharp
public class Question
{
    // ...existing fields unchanged...
    public QuestionType Type { get; set; }
    public string DifficultyLevel { get; set; } = string.Empty; // no Java equivalent, kept for Python only — see table above
}
```

**2. New enum, mirrors Java's `QuestionType.java` exactly** (same 5 members, same order, names
matching Java's constant names so the mapping is obviously 1:1 — put it in `Models/` next to
`Question.cs`):

```csharp
public enum QuestionType
{
    ReadAloud,
    ShortAnswer,
    LongAnswer,
    Opinion,
    Description
}
```

**3. `Models/ExamPaperQuestion.cs`** — remove the `QuestionType`/`DifficultyLevel` string
properties (they move to `Question`, see #1); `ExamPaperQuestion` goes back to being a pure
structural wrapper (`Id`, `OrderIndex`, `AttemptAnswerId`, `Question`, `EvaluationGuide`),
matching how Java composes an exam-paper-question around a `Question` without duplicating its
fields.

**4. `Services/MockExamDataFactory.cs`** — re-map every mock question's invented type string to
the real `QuestionType` value that actually matches its content (judgment call on the math
paper, see below; the English paper maps cleanly from the IELTS-style part structure already
implied by the existing `Code`s):

| Question | Current (wrong) string | Real `QuestionType` | Why |
|---|---|---|---|
| ENG-SPEAK-P1-Q1 (hometown) | `"personal_introduction"` | `ShortAnswer` | IELTS Part 1, 25-45s window |
| ENG-SPEAK-P1-Q2 (relax routine) | `"personal_habit"` | `ShortAnswer` | same Part 1 shape |
| ENG-SPEAK-P2-Q1 (learned something useful) | `"long_turn"` | `LongAnswer` | literally IELTS's "long turn", 60s prep + 60-120s response |
| ENG-SPEAK-P3-Q1 (experience vs. books) | `"opinion_discussion"` | `Opinion` | asks for a developed opinion/comparison |
| ENG-SPEAK-P3-Q2 (life skills vs. academics) | `"policy_discussion"` | `Opinion` | "do you think... why" — opinion |
| MATH-APP-Q1 (solve system of equations) | `"problem_solving"` | `ShortAnswer` | short, direct solution; judgment call — `Description` is the alternative if you'd rather emphasize "explain each step" |
| MATH-APP-Q2 (interpret a derivative) | `"applied_reasoning"` | `Description` | explicitly asks to explain what something *means*, not just solve |

Move `DifficultyLevel` values (`"easy"`/`"medium"`) onto `Question` too — they're already valid
Python `DifficultyLevel` values, no change needed to the strings themselves, just where they
live.

**5. `State/ExamSessionState.cs`** — once `Type`/`DifficultyLevel` live on `Question`, the
`QuestionTypesByQuestionId`/`DifficultyLevelsByQuestionId` dictionaries become redundant
(`LoadMockExam` populated them from `ExamPaperQuestion.QuestionType`/`.DifficultyLevel`, which no
longer exist). Delete both dictionaries and `LoadMockExam`'s two `.ToDictionary(...)` calls that
built them. `EvaluationGuidesByQuestionId` is unaffected (that one's fine as-is — keyed lookup
makes sense since `EvaluationGuide` is a separate linked entity, same as Java).

**6. `Services/TavusFullPipelineExamFlowService.cs`, `BuildConversationalContext`** — read
`question.Type`/`question.DifficultyLevel` directly instead of the
`_sessionState.QuestionTypesByQuestionId.TryGetValue(...)` dance, and convert the `QuestionType`
enum to the exact lowercase snake_case string Python's `QuestionType` enum expects (`ReadAloud`→
`"read_aloud"`, `ShortAnswer`→`"short_answer"`, `LongAnswer`→`"long_answer"`, `Opinion`→
`"opinion"`, `Description`→`"description"` — see `agents/src/schemas/enums.py` for the Python
side, already confirmed these 5 exact values). This is the same kind of converter
`UppercaseEnumJsonConverter<TurnType>` already does for the Java-bound `TurnType` field
(`ShouldFollowupRequest.cs`) — same pattern, opposite casing, different enum. A small
`private static string ToPythonQuestionType(QuestionType type) => type switch { ... }` is enough,
no need for a generic `JsonConverter` since this is built into a plain string interpolation today,
not JSON-serialized via `System.Text.Json` attributes.

Same applies to whatever rebuilds `EvaluateTurnRequest.Question` (`QuestionContextDto`) per
Lệnh 3b in `ai-examiner-plan.md` — use the same conversion function in both places instead of
duplicating the switch.

Drop the `"speaking"`/`"medium"` fallback defaults currently in `BuildConversationalContext` —
once every mock question has a real `Type`, there's nothing left to fall back from; an
unset value would now mean a real data bug, not an expected gap.

## Verification

- `dotnet build`.
- For each of the 7 mock questions, confirm `BuildConversationalContext`'s `<question_context>`
  JSON now contains one of exactly: `"read_aloud"`, `"short_answer"`, `"long_answer"`,
  `"opinion"`, `"description"` for `question_type` — never `"speaking"` or any of the old
  invented strings.
- Confirm `dotnet build` still succeeds with `ExamSessionState`'s two dictionaries removed (no
  leftover references in `BuildConversationalContext` or elsewhere).
