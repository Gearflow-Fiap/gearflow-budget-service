using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Domain.Budgets;
using Reqnroll;

namespace GearFlow.Billing.BddTests.Features;

[Binding]
public sealed class DecisaoDoOrcamentoSteps
{
    private readonly BudgetScenarios.Store store = new();
    private GenerateBudgetCommand? proposal;
    private Guid id;
    private bool rejected;

    [Given("um diagnóstico válido para uma ordem de serviço")]
    public void Diagnostic() => proposal = BudgetScenarios.Proposal(store);

    [Given("um orçamento pendente com link válido")]
    public async Task Pending()
    { proposal = BudgetScenarios.Proposal(store); id = (await BudgetScenarios.Generate(store).HandleAsync(proposal, default)).Value; }

    [When("a mesma geração de orçamento é solicitada duas vezes")]
    public async Task GenerateTwice()
    {
        var first = await BudgetScenarios.Generate(store).HandleAsync(proposal!, default);
        var repeated = await BudgetScenarios.Generate(store).HandleAsync(proposal!, default);
        Assert.True(first.IsSuccess); Assert.Equal(first.Value, repeated.Value);
    }
    [Then("existe apenas um orçamento, um evento e uma intenção de e-mail")]
    public void OneProposal() { Assert.Single(store.Budgets); Assert.Single(store.Outbox); Assert.Single(store.Notifications); }

    [When("o cliente aprova duas vezes e depois tenta rejeitar")]
    public async Task ApproveTwice()
    {
        var handler = BudgetScenarios.Review(store);
        Assert.True((await handler.HandleAsync(new ApproveBudgetCommand(id, "valid-test-token"), default)).IsSuccess);
        Assert.True((await handler.HandleAsync(new ApproveBudgetCommand(id, "valid-test-token"), default)).IsSuccess);
        rejected = !(await handler.HandleAsync(new RejectBudgetCommand(id, "valid-test-token"), default)).IsSuccess;
    }
    [Then("a aprovação é preservada com um único evento de decisão")]
    public void OneDecision()
    { Assert.True(rejected); Assert.Equal(BudgetStatus.Approved, store.Budgets[id].Status); Assert.Single(store.Outbox.OfType<BudgetApproved>()); }

    [When("alguém tenta aprovar com token adulterado")]
    public async Task Tamper() => rejected = !(await BudgetScenarios.Review(store).HandleAsync(new ApproveBudgetCommand(id, "tampered"), default)).IsSuccess;
    [Then("o orçamento continua pendente e não publica aprovação")]
    public void NoDecision()
    { Assert.True(rejected); Assert.Equal(BudgetStatus.Pending, store.Budgets[id].Status); Assert.Empty(store.Outbox.OfType<BudgetApproved>()); }
}
