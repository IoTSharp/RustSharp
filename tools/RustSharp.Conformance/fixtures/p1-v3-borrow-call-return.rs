// frozen P1 fixture: borrow-call-return
fn identity(value: &i32) -> &i32 { value }
fn main() {
    let value = 7;
    let reference = identity(&value);
    println!("{}", *reference);
}
