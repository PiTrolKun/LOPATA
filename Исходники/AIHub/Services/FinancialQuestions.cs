namespace AIHub.Services;

public sealed record FinancialQuestion(string Id, string Category, bool Required = false, bool Income = false);
public static class FinancialQuestions
{
    public static readonly string[] Statuses = ["school", "student", "employed", "unemployed", "retired", "parental"];
    public static IReadOnlyList<FinancialQuestion> All { get; } = Array.AsReadOnly<FinancialQuestion>(
    [
        Q("water_gas", "required", true), Q("electricity", "required", true), Q("utilities_other", "required", true),
        Q("housing", "required", true), Q("food", "required", true), Q("required_other", "required", true),
        Q("phone", "communication"), Q("internet", "communication"),
        Q("public_transport", "transport"), Q("taxi", "transport"), Q("fuel", "transport"), Q("parking", "transport"), Q("car_wash", "transport"),
        Q("medicine", "health"), Q("contraception", "health"), Q("sport", "health"), Q("lenses", "health"),
        Q("vpn", "subscriptions"), Q("streaming", "subscriptions"), Q("bank_marketplace", "subscriptions"), Q("ai", "subscriptions"), Q("subscriptions_other", "subscriptions"),
        Q("nicotine", "pleasures"), Q("alcohol", "pleasures"), Q("soda", "pleasures"), Q("energy_drinks", "pleasures"),
        Q("fastfood", "pleasures"), Q("sushi", "pleasures"), Q("pizza", "pleasures"), Q("restaurants", "pleasures"), Q("sweets", "pleasures"), Q("hobbies", "pleasures"),
        Q("hygiene", "personal"), Q("cosmetics", "personal"), Q("perfume", "personal"), Q("haircut", "personal"), Q("care_other", "personal"),
        Q("pet_food", "pets"), Q("pet_litter", "pets"), Q("pet_care", "pets"), Q("pets_other", "pets"),
        Q("tuition", "education"), Q("courses", "education"), Q("tutor", "education"), Q("education_services", "education"), Q("education_other", "education"),
        Q("parents", "relatives"), Q("children", "relatives"), Q("transfers", "relatives"), Q("relatives_other", "relatives"),
        Q("additional", "additional"), Q("stable_income", "income", income: true), Q("additional_income", "income", income: true)
    ]);
    private static FinancialQuestion Q(string id, string category, bool required = false, bool income = false) => new(id, category, required, income);
    public static FinancialQuestion Get(string id) => All.First(q => q.Id == id);
}
