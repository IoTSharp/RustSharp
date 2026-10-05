// frozen P1 fixture: borrow-nll-branch
fn main() {
    let mut value = 1;
    if true {
        let view = &value;
        println!("{}", *view);
    }
    value = 2;
    println!("{}", value);
}
