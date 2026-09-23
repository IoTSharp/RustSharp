fn main() {
    let value = 7;
    let shared = &value;
    println!("{}", *shared);
}
