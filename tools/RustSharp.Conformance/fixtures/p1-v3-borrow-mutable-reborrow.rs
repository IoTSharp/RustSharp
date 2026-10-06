// frozen P1 fixture: borrow-mutable-reborrow
fn main() {
    let mut value: i32 = 7;
    {
        let shared = &mut value;
        *shared = 9;
    }
    println!("{}", value);
}
