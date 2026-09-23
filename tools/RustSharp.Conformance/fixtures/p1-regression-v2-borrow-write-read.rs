fn main() {
    let mut value: i32 = 1;
    let reference = &mut value;
    *reference = 9;
    println!("{}", *reference);
    println!("{}", value);
}
