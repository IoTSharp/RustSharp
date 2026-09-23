fn main() {
    let value: i32 = 7;
    let shared = &value;
    println!("{}", *shared);
}
