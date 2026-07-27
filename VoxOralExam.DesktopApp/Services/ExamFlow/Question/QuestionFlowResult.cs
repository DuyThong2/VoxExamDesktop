namespace VoxOralExam.DesktopApp.Services.ExamFlow.Question;

internal sealed record QuestionFlowResult(
    bool Completed,
    int AssessmentTurnCount,
    int LastTurnOrder);
