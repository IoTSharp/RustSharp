// frozen P1 fixture: borrow-budget-limit
fn main() {
    let mut value = 0;
    {
        let first = &mut value;
        *first += 1;
    }
    {
        let second = &mut value;
        *second += 2;
    }
    {
        let third = &mut value;
        *third += 3;
    }
    println!("{}", value);
}
