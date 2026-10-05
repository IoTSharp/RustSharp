// frozen P1 fixture: borrow-aggregate-copy
fn main() {
    let pair = (1, 2);
    let copy = pair;
    println!("{}", pair.0);
    println!("{}", copy.1);
}
