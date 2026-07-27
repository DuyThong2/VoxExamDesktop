using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.Mocks;

public class MockExamDataFactory
{
    public IReadOnlyList<Exam> GetAvailableExams()
    {
        return GetExamPapers()
            .Select(paper => new Exam
            {
                Id = paper.ExamId.ToString(),
                Title = paper.Title,
                Subject = paper.Subject,
                Description = paper.Description,
                Duration = paper.DurationMinutes,
                ExamDate = paper.ExamDate,
                Status = paper.Status,
                Kind = paper.ExamId == Guid.Parse("11111111-1111-1111-1111-111111111111")
                    ? ExamKind.ClassTest
                    : ExamKind.Centralized,
                CanEnter = paper.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase),
                EntryMessage = paper.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : "Chua den luc vao thi cho bai nay.",
            })
            .ToList();
    }

    public ExamPaper CreateMockPaperForExam(string? examId = null)
    {
        var papers = GetExamPapers();

        if (Guid.TryParse(examId, out var parsedExamId))
        {
            var matched = papers.FirstOrDefault(paper => paper.ExamId == parsedExamId);
            if (matched is not null)
            {
                return matched;
            }
        }

        return papers[0];
    }

    private static List<ExamPaper> GetExamPapers()
    {
        var today = DateTime.Today;

        var englishExamId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var mathExamId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        return
        [
            new ExamPaper
            {
                ExamId = englishExamId,
                ExamPaperId = Guid.Parse("11111111-aaaa-1111-aaaa-111111111111"),
                ExamAttemptId = Guid.Parse("11111111-bbbb-1111-bbbb-111111111111"),
                Title = "Quick English Speaking Mock",
                Subject = "English Speaking",
                Description = "Very easy primary-school English speaking mock with short answers and one slightly longer prompt to test whether follow-up works.",
                DurationMinutes = 5,
                ExamDate = today.AddHours(9),
                ScheduleEndAt = today.AddHours(9).AddMinutes(5),
                Status = "in_progress",
                PaperQuestions =
                [
                    BuildEnglishQuestion(
                        paperQuestionId: "11111111-0001-0001-0001-111111111111",
                        attemptAnswerId: "11111111-1001-1001-1001-111111111111",
                        questionId: "11111111-2001-2001-2001-111111111111",
                        code: "ENG-SPEAK-Q1",
                        orderIndex: 1,
                        type: QuestionType.ShortAnswer,
                        difficultyLevel: "easy",
                        instructionText: "Answer in one or two short sentences.",
                        questionText: "What is your name?",
                        promptText: "Warm-up: name.",
                        preparationText: string.Empty,
                        preparationTimeSeconds: 0,
                        minResponseSeconds: 3,
                        maxResponseSeconds: 8,
                        evaluationGuide: new QuestionEvaluationGuide
                        {
                            Id = Guid.Parse("11111111-3001-3001-3001-111111111111"),
                            QuestionId = Guid.Parse("11111111-2001-2001-2001-111111111111"),
                            ExpectedContent = "States the speaker's name clearly.",
                            KeyPoints = "name",
                            AcceptableResponses = "Any short answer that gives a name is acceptable.",
                            OffTopicExamples = "Talking about age, school, or favorite things without giving a name.",
                            ScoringHints = "Reward a clear, direct answer.",
                            CommonMistakes = "Speaking too softly or not saying a name clearly."
                        }),
                    BuildEnglishQuestion(
                        paperQuestionId: "11111111-0002-0002-0002-111111111111",
                        attemptAnswerId: "11111111-1002-1002-1002-111111111111",
                        questionId: "11111111-2002-2002-2002-111111111111",
                        code: "ENG-SPEAK-Q2",
                        orderIndex: 2,
                        type: QuestionType.ShortAnswer,
                        difficultyLevel: "easy",
                        instructionText: "Answer in one or two short sentences.",
                        questionText: "What is your favorite color?",
                        promptText: "Warm-up: favorite color.",
                        preparationText: string.Empty,
                        preparationTimeSeconds: 0,
                        minResponseSeconds: 3,
                        maxResponseSeconds: 8,
                        evaluationGuide: new QuestionEvaluationGuide
                        {
                            Id = Guid.Parse("11111111-3002-3002-3002-111111111111"),
                            QuestionId = Guid.Parse("11111111-2002-2002-2002-111111111111"),
                            ExpectedContent = "Names one favorite color.",
                            KeyPoints = "color",
                            AcceptableResponses = "Any simple color word is acceptable.",
                            OffTopicExamples = "Talking about clothes or toys without naming a color.",
                            ScoringHints = "Reward a clear, natural answer even if it is very short.",
                            CommonMistakes = "Not saying a color clearly."
                        }),
                    BuildEnglishQuestion(
                        paperQuestionId: "11111111-0003-0003-0003-111111111111",
                        attemptAnswerId: "11111111-1003-1003-1003-111111111111",
                        questionId: "11111111-2003-2003-2003-111111111111",
                        code: "ENG-SPEAK-Q3",
                        orderIndex: 3,
                        type: QuestionType.ShortAnswer,
                        difficultyLevel: "easy",
                        instructionText: "Answer in one or two short sentences.",
                        questionText: "Do you like cats or dogs?",
                        promptText: "Warm-up: cats or dogs.",
                        preparationText: string.Empty,
                        preparationTimeSeconds: 0,
                        minResponseSeconds: 3,
                        maxResponseSeconds: 8,
                        evaluationGuide: new QuestionEvaluationGuide
                        {
                            Id = Guid.Parse("11111111-3003-3003-3003-111111111111"),
                            QuestionId = Guid.Parse("11111111-2003-2003-2003-111111111111"),
                            ExpectedContent = "Chooses cats or dogs and may give a simple reason.",
                            KeyPoints = "preference",
                            AcceptableResponses = "Either animal is acceptable.",
                            OffTopicExamples = "Listing many animals without choosing cats or dogs.",
                            ScoringHints = "Reward a clear choice.",
                            CommonMistakes = "Not choosing one."
                        }),
                    BuildEnglishQuestion(
                        paperQuestionId: "11111111-0004-0004-0004-111111111111",
                        attemptAnswerId: "11111111-1004-1004-1004-111111111111",
                        questionId: "11111111-2004-2004-2004-111111111111",
                        code: "ENG-SPEAK-Q4",
                        orderIndex: 4,
                        type: QuestionType.LongAnswer,
                        difficultyLevel: "medium",
                        instructionText: "Speak for a few short sentences. Say what it is and why you like it.",
                        questionText: "Tell me about your favorite toy or game.",
                        promptText: "Slightly longer answer about a favorite toy or game.",
                        preparationText: "Think about its name, color, and why you like it.",
                        preparationTimeSeconds: 3,
                        minResponseSeconds: 10,
                        maxResponseSeconds: 25,
                        evaluationGuide: new QuestionEvaluationGuide
                        {
                            Id = Guid.Parse("11111111-3004-3004-3004-111111111111"),
                            QuestionId = Guid.Parse("11111111-2004-2004-2004-111111111111"),
                            ExpectedContent = "Describes one toy or game with one or two simple details.",
                            KeyPoints = "name, detail, reason",
                            AcceptableResponses = "Any toy or game is acceptable if the child can say at least one detail about it.",
                            OffTopicExamples = "Giving only the name with no extra detail at all.",
                            ScoringHints = "Reward simple details because they give the examiner room for a natural follow-up.",
                            CommonMistakes = "Answering too briefly to develop any follow-up path."
                        }),
                    BuildEnglishQuestion(
                        paperQuestionId: "11111111-0005-0005-0005-111111111111",
                        attemptAnswerId: "11111111-1005-1005-1005-111111111111",
                        questionId: "11111111-2005-2005-2005-111111111111",
                        code: "ENG-SPEAK-Q5",
                        orderIndex: 5,
                        type: QuestionType.Opinion,
                        difficultyLevel: "easy",
                        instructionText: "Give a short opinion with one reason.",
                        questionText: "Do you like ice cream? Why?",
                        promptText: "Opinion: ice cream.",
                        preparationText: string.Empty,
                        preparationTimeSeconds: 0,
                        minResponseSeconds: 5,
                        maxResponseSeconds: 12,
                        evaluationGuide: new QuestionEvaluationGuide
                        {
                            Id = Guid.Parse("11111111-3005-3005-3005-111111111111"),
                            QuestionId = Guid.Parse("11111111-2005-2005-2005-111111111111"),
                            ExpectedContent = "States yes or no and gives one simple reason.",
                            KeyPoints = "preference, reason",
                            AcceptableResponses = "Any simple yes or no answer with a reason is acceptable.",
                            OffTopicExamples = "Talking about food in general without saying if they like ice cream.",
                            ScoringHints = "Reward a clear choice and a natural reason.",
                            CommonMistakes = "Saying only yes or no with no reason."
                        })
                ]
            },
            new ExamPaper
            {
                ExamId = mathExamId,
                ExamPaperId = Guid.Parse("22222222-aaaa-2222-aaaa-222222222222"),
                ExamAttemptId = Guid.Parse("22222222-bbbb-2222-bbbb-222222222222"),
                Title = "Ky thi van dap Toan ung dung (rut gon)",
                Subject = "Toan",
                Description = "Mock exam rut gon de test nhanh pipeline tung turn.",
                DurationMinutes = 3,
                ExamDate = today.AddDays(1).AddHours(14),
                ScheduleEndAt = today.AddDays(1).AddHours(14).AddMinutes(3),
                Status = "upcoming",
                PaperQuestions =
                [
                    new ExamPaperQuestion
                    {
                        Id = Guid.Parse("22222222-0001-0001-0001-222222222222"),
                        OrderIndex = 1,
                        AttemptAnswerId = Guid.Parse("22222222-1001-1001-1001-222222222222"),
                        Question = new Question
                        {
                            Id = Guid.Parse("22222222-2001-2001-2001-222222222222"),
                            Code = "MATH-APP-Q1",
                            Type = QuestionType.ShortAnswer,
                            DifficultyLevel = "medium",
                            InstructionText = "Trinh bay ngan gon cach giai.",
                            QuestionText = "Giai he phuong trinh: 2x + y = 7 va x - y = 2.",
                            PromptText = "Bai toan giai he phuong trinh co ban.",
                            PreparationText = string.Empty,
                            PreparationTimeSeconds = 0,
                            MinResponseSeconds = 10,
                            MaxResponseSeconds = 25
                        }
                    },
                    new ExamPaperQuestion
                    {
                        Id = Guid.Parse("22222222-0002-0002-0002-222222222222"),
                        OrderIndex = 2,
                        AttemptAnswerId = Guid.Parse("22222222-1002-1002-1002-222222222222"),
                        Question = new Question
                        {
                            Id = Guid.Parse("22222222-2002-2002-2002-222222222222"),
                            Code = "MATH-APP-Q2",
                            Type = QuestionType.Description,
                            DifficultyLevel = "medium",
                            InstructionText = "Giai thich ngan gon y nghia thuc te.",
                            QuestionText = "Neu ham so mo ta chi phi san xuat la C(x) = x^2 + 4x + 10, dao ham C'(x) cho ta thong tin gi?",
                            PromptText = "Giai thich y nghia thuc te cua dao ham chi phi.",
                            PreparationText = "Goi y: nghi ve toc do thay doi cua chi phi.",
                            PreparationTimeSeconds = 5,
                            MinResponseSeconds = 10,
                            MaxResponseSeconds = 25
                        }
                    }
                ]
            }
        ];
    }

    private static ExamPaperQuestion BuildEnglishQuestion(
        string paperQuestionId,
        string attemptAnswerId,
        string questionId,
        string code,
        int orderIndex,
        QuestionType type,
        string difficultyLevel,
        string instructionText,
        string questionText,
        string promptText,
        string preparationText,
        int preparationTimeSeconds,
        int minResponseSeconds,
        int maxResponseSeconds,
        QuestionEvaluationGuide evaluationGuide)
    {
        var parsedQuestionId = Guid.Parse(questionId);

        return new ExamPaperQuestion
        {
            Id = Guid.Parse(paperQuestionId),
            OrderIndex = orderIndex,
            AttemptAnswerId = Guid.Parse(attemptAnswerId),
            EvaluationGuide = evaluationGuide,
            Question = new Question
            {
                Id = parsedQuestionId,
                Code = code,
                Type = type,
                DifficultyLevel = difficultyLevel,
                InstructionText = instructionText,
                QuestionText = questionText,
                PromptText = promptText,
                PreparationText = preparationText,
                PreparationTimeSeconds = preparationTimeSeconds,
                MinResponseSeconds = minResponseSeconds,
                MaxResponseSeconds = maxResponseSeconds
            }
        };
    }
}

