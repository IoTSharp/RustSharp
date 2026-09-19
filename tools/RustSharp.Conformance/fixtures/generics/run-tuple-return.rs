fn pair<T, U>(left: T, right: U) -> (T, U) { (left, right) }
fn main() {
    let result = pair(42, true);
    println!("{}", result.0);
    println!("{}", result.1);
}
