fn main() {
    let mut value = 1;
    let first = &mut value;
    let second = &mut value;
    *second = 2;
    println!("{}", *first);
}
